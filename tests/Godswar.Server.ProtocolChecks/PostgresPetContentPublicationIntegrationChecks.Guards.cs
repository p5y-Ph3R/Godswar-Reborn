using Npgsql;
using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetContentPublicationIntegrationChecks
{
    private static readonly (string Table, string Assertion)[]
        GuardedPetContentTables =
    [
        ("pet_content_settings", "settings"),
        ("pet_content_species_definitions", "species"),
        ("pet_content_aptitude_definitions", "aptitudes"),
        ("pet_content_native_profiles", "native profiles"),
        ("pet_content_experience_steps", "experience steps"),
        ("pet_content_rebirth_steps", "rebirth steps"),
        ("pet_content_merge_savvy_steps", "merge Savvy steps"),
        ("pet_content_merge_savvy_lookup", "Merge-savvy lookup"),
        ("pet_content_hatch_rank_steps", "hatch-rank steps"),
        ("pet_content_merge_rank_lookup", "Merge-rank lookup"),
        ("pet_content_merge_rank_species_factors",
            "Merge-rank species factors"),
        ("pet_content_merge_rank_spirit_steps", "Merge-rank spirit steps")
    ];

    private static async Task AssertReviewedPredecessorUpgradeAsync(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog itemCatalog,
        string currentRevision)
    {
        var v4Revision = NewRevision();
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction =
                     await connection.BeginTransactionAsync())
        {
            await using (var header = new NpgsqlCommand(
                """
                INSERT INTO pet_content_revisions (
                    revision, species_count, aptitude_count,
                    native_profile_count, experience_step_count,
                    rebirth_step_count, source)
                SELECT @v4Revision,
                       species_count,
                       aptitude_count,
                       native_profile_count,
                       experience_step_count,
                       rebirth_step_count,
                       'reviewed-pet-baseline-v4'
                FROM pet_content_revisions
                WHERE revision = @currentRevision;
                """,
                connection,
                transaction))
            {
                header.Parameters.AddWithValue("v4Revision", v4Revision);
                header.Parameters.AddWithValue(
                    "currentRevision",
                    currentRevision);
                Check.Equal(
                    1,
                    await header.ExecuteNonQueryAsync(),
                    "V4 upgrade fixture creates one predecessor manifest");
            }

            foreach (var (table, _) in GuardedPetContentTables)
            {
                if (table is "pet_content_merge_savvy_steps" or
                    "pet_content_merge_savvy_lookup" or
                    "pet_content_hatch_rank_steps" or
                    "pet_content_merge_rank_lookup" or
                    "pet_content_merge_rank_species_factors" or
                    "pet_content_merge_rank_spirit_steps")
                {
                    continue;
                }

                await CopyRevisionDefinitionsAsync(
                    connection,
                    transaction,
                    table,
                    currentRevision,
                    v4Revision,
                    omitCalmProfiles: false);
            }

            await using (var publish = new NpgsqlCommand(
                """
                UPDATE pet_content_publication
                SET revision = @v4Revision,
                    published_at = now()
                WHERE family = 'pets';
                """,
                connection,
                transaction))
            {
                publish.Parameters.AddWithValue("v4Revision", v4Revision);
                Check.Equal(
                    1,
                    await publish.ExecuteNonQueryAsync(),
                    "V4 upgrade fixture becomes the official publication");
            }

            await transaction.CommitAsync();
        }

        var upgraded = await PostgresPetContentBaselinePublisher
            .EnsurePublishedAsync(dataSource, itemCatalog);
        var loaded = await PostgresPetContentReader.LoadAsync(
            dataSource,
            itemCatalog);
        Check.True(
            upgraded.Created &&
            upgraded.Revision == currentRevision &&
            loaded.Revision.Source == PetContentBaseline.Source &&
            loaded.Aptitudes.All(static aptitude =>
                aptitude.InnateTalentMask == (aptitude.Aptitude >= 14
                    ? 31
                    : aptitude.Aptitude >= 10
                        ? 26
                        : 0)) &&
            loaded.TryGetNativeProfile(
                speciesId: 1,
                aptitude: (short)PetAptitude.Calm,
                out _),
            "live startup advances a sealed reviewed predecessor to V10");

        var repeated = await PostgresPetContentBaselinePublisher
            .EnsurePublishedAsync(dataSource, itemCatalog);
        Check.True(
            !repeated.Created && repeated.Revision == currentRevision,
            "sealed V10 pet content is reused idempotently");
    }

    private static async Task CopyRevisionDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string sourceRevision,
        string targetRevision,
        bool omitCalmProfiles)
    {
        if (!GuardedPetContentTables.Any(
                candidate => candidate.Table == table))
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }

        var calmFilter = omitCalmProfiles
            ? "AND source_row.aptitude <> @calm"
            : string.Empty;
        var sql = $"""
            INSERT INTO public.{table}
            SELECT (
                jsonb_populate_record(
                    NULL::public.{table},
                    to_jsonb(source_row) ||
                        jsonb_build_object('revision', @targetRevision)
                )
            ).*
            FROM public.{table} source_row
            WHERE source_row.revision = @sourceRevision
            {calmFilter};
            """;
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue("targetRevision", targetRevision);
        command.Parameters.AddWithValue("sourceRevision", sourceRevision);
        if (omitCalmProfiles)
        {
            command.Parameters.AddWithValue(
                "calm",
                (short)PetAptitude.Calm);
        }

        var expected = table switch
        {
            "pet_content_settings" => 1,
            "pet_content_native_profiles" when omitCalmProfiles =>
                PetNativeAptitudeProfileCatalog.ProfileCount,
            _ => Convert.ToInt32(await ReadScalarAsync<long>(
                connection,
                transaction,
                $"SELECT count(*) FROM public.{table} " +
                    "WHERE revision = @revision;",
                ("revision", sourceRevision)))
        };
        Check.Equal(
            expected,
            await command.ExecuteNonQueryAsync(),
            $"reviewed predecessor fixture copies {table}");
    }

    private static async Task<T> ReadScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        AddParameters(command, parameters);
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException(
                "The pet-content fixture query returned no value."));
    }

    private static async Task AssertDeclaredCountGuardsAsync(
        NpgsqlDataSource dataSource,
        string officialRevision)
    {
        foreach (var (table, assertion) in GuardedPetContentTables)
        {
            await AssertTableOverflowRejectedAsync(
                dataSource,
                officialRevision,
                table,
                assertion);
        }
    }

    private static async Task AssertTableOverflowRejectedAsync(
        NpgsqlDataSource dataSource,
        string officialRevision,
        string table,
        string assertion)
    {
        var revision = NewRevision();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await InsertTestRevisionAsync(connection, transaction, revision);
            if (table == "pet_content_native_profiles")
            {
                await CopyOneDefinitionAsync(
                    connection,
                    transaction,
                    "pet_content_species_definitions",
                    officialRevision,
                    revision);
                await CopyOneDefinitionAsync(
                    connection,
                    transaction,
                    "pet_content_aptitude_definitions",
                    officialRevision,
                    revision);
            }
            else if (table is "pet_content_merge_savvy_steps" or
                     "pet_content_hatch_rank_steps")
            {
                await CopyRankAptitudeDefinitionAsync(
                    connection,
                    transaction,
                    officialRevision,
                    revision,
                    table);
            }
            else if (table == "pet_content_merge_rank_species_factors")
            {
                await CopyRankSpeciesDefinitionAsync(
                    connection,
                    transaction,
                    officialRevision,
                    revision);
            }

            await CopyOneDefinitionAsync(
                connection,
                transaction,
                table,
                officialRevision,
                revision);
            await CopyOneDefinitionAsync(
                connection,
                transaction,
                table,
                officialRevision,
                revision);
        }
        catch (PostgresException exception) when (
            exception.MessageText.Contains(
                "exceeds declared count",
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync();
            return;
        }

        await transaction.RollbackAsync();
        throw new InvalidOperationException(
            $"Pet-content {assertion} exceeded its declared count.");
    }

    private static async Task InsertTestRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_content_revisions (
                revision, species_count, aptitude_count,
                native_profile_count, experience_step_count,
                rebirth_step_count, merge_savvy_step_count,
                merge_savvy_lookup_count,
                hatch_rank_step_count, merge_rank_lookup_count,
                merge_rank_species_factor_count,
                merge_rank_spirit_step_count, source)
            VALUES (@revision, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                    'declared-count-test');
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CopyOneDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string sourceRevision,
        string targetRevision)
    {
        if (!GuardedPetContentTables.Any(
                candidate => candidate.Table == table))
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }

        var sql = $"""
            INSERT INTO public.{table}
            SELECT (
                jsonb_populate_record(
                    NULL::public.{table},
                    to_jsonb(source_row) ||
                        jsonb_build_object('revision', @targetRevision)
                )
            ).*
            FROM public.{table} source_row
            WHERE source_row.revision = @sourceRevision
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue("targetRevision", targetRevision);
        command.Parameters.AddWithValue("sourceRevision", sourceRevision);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"guard fixture copied one {table} row");
    }

    private static async Task CopyRankAptitudeDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceRevision,
        string targetRevision,
        string rankTable)
    {
        if (rankTable is not "pet_content_merge_savvy_steps" and
            not "pet_content_hatch_rank_steps")
        {
            throw new ArgumentOutOfRangeException(nameof(rankTable));
        }

        var sql = $"""
            INSERT INTO public.pet_content_aptitude_definitions
            SELECT (
                jsonb_populate_record(
                    NULL::public.pet_content_aptitude_definitions,
                    to_jsonb(source_row) ||
                        jsonb_build_object('revision', @targetRevision)
                )
            ).*
            FROM public.pet_content_aptitude_definitions source_row
            WHERE source_row.revision = @sourceRevision
              AND source_row.aptitude = (
                  SELECT aptitude
                  FROM public.{rankTable}
                  WHERE revision = @sourceRevision
                  ORDER BY aptitude
                  LIMIT 1
              );
            """;
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue("targetRevision", targetRevision);
        command.Parameters.AddWithValue("sourceRevision", sourceRevision);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"guard fixture copied the {rankTable} aptitude row");
    }

    private static async Task CopyRankSpeciesDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceRevision,
        string targetRevision)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.pet_content_species_definitions
            SELECT (
                jsonb_populate_record(
                    NULL::public.pet_content_species_definitions,
                    to_jsonb(source_row) ||
                        jsonb_build_object('revision', @targetRevision)
                )
            ).*
            FROM public.pet_content_species_definitions source_row
            WHERE source_row.revision = @sourceRevision
              AND source_row.species_id = (
                  SELECT species_id
                  FROM public.pet_content_merge_rank_species_factors
                  WHERE revision = @sourceRevision
                  ORDER BY species_id
                  LIMIT 1
              );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("targetRevision", targetRevision);
        command.Parameters.AddWithValue("sourceRevision", sourceRevision);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "guard fixture copied the Merge-rank species row");
    }
}
