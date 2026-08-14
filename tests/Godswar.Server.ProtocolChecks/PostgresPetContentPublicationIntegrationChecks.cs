using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetContentPublicationIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL immutable pet-content publication";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var itemCatalog =
            await PostgresItemTemplateContentBootstrapper.LoadAsync(
                connectionString);
        var first = await PostgresPetContentBootstrapper.LoadAsync(
            connectionString,
            itemCatalog);
        Check.True(
            first.Revision.Sha256.Length == 64 &&
            first.Revision.SpeciesCount == first.Species.Count &&
            first.Revision.AptitudeCount == first.Aptitudes.Count &&
            first.Revision.NativeProfileCount ==
                first.NativeProfiles.Count &&
            first.Revision.ExperienceStepCount ==
                first.ExperienceSteps.Count &&
            first.Revision.RebirthStepCount == first.RebirthSteps.Count &&
            first.Revision.MergeSavvyStepCount ==
                first.MergeSavvySteps.Count &&
            first.Revision.MergeSavvyLookupCount ==
                first.MergeSavvyLookup.Count &&
            first.Revision.HatchRankStepCount ==
                first.HatchRankSteps.Count &&
            first.Revision.MergeRankLookupCount ==
                first.MergeRankLookup.Count &&
            first.Revision.MergeRankSpeciesFactorCount ==
                first.MergeRankSpeciesFactors.Count &&
            first.Revision.MergeRankSpiritStepCount ==
                first.MergeRankSpiritSteps.Count,
            "pet bootstrap pins one complete SHA-256 manifest");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var repeated = await PostgresPetContentBaselinePublisher
            .EnsurePublishedAsync(dataSource, itemCatalog);
        Check.True(
            !repeated.Created &&
            repeated.Revision == first.Revision.Sha256,
            "pet publication is idempotent after cold bootstrap");
        await AssertReviewedPredecessorUpgradeAsync(
            dataSource,
            itemCatalog,
            first.Revision.Sha256);
        await AssertMagicJadeAppearanceGroupsAsync(
            dataSource,
            first.Revision.Sha256);

        await AssertSourceMutationIgnoredAsync(
            dataSource,
            itemCatalog,
            first);
        await AssertStableSourceIdentityCannotDisappearAsync(
            dataSource,
            first.Species[0].SpeciesId);
        await AssertSealedRowsRejectMutationAsync(
            dataSource,
            first.Revision.Sha256);
        await AssertIncompletePublicationRejectedAsync(dataSource);
        await AssertDeclaredCountGuardsAsync(
            dataSource,
            first.Revision.Sha256);
    }

    private static async Task AssertSourceMutationIgnoredAsync(
        NpgsqlDataSource dataSource,
        Godswar.Server.Application.Items.IItemTemplateCatalog itemCatalog,
        Godswar.Server.Application.Pets.PinnedPetContentCatalog first)
    {
        var species = first.Species[0];
        var aptitude = first.Aptitudes[0];
        var sourceSpeciesName = await ReadScalarAsync<string>(
            dataSource,
            "SELECT display_name FROM pet_templates " +
            "WHERE species_id = @id;",
            ("id", species.SpeciesId));
        var sourceAptitudeName = await ReadScalarAsync<string>(
            dataSource,
            "SELECT display_name FROM pet_aptitude_templates " +
            "WHERE aptitude = @id;",
            ("id", aptitude.Aptitude));

        try
        {
            await ExecuteAsync(
                dataSource,
                "UPDATE pet_templates SET display_name = @name " +
                "WHERE species_id = @id;",
                ("name", sourceSpeciesName + " decoy"),
                ("id", species.SpeciesId));
            await ExecuteAsync(
                dataSource,
                "UPDATE pet_aptitude_templates SET display_name = @name " +
                "WHERE aptitude = @id;",
                ("name", sourceAptitudeName + " decoy"),
                ("id", aptitude.Aptitude));

            var reloaded = await PostgresPetContentReader.LoadAsync(
                dataSource,
                itemCatalog);
            Check.True(
                reloaded.Revision.Sha256 == first.Revision.Sha256 &&
                reloaded.Species[0].DisplayName == species.DisplayName &&
                reloaded.Aptitudes[0].DisplayName == aptitude.DisplayName,
                "non-key source-table mutation cannot change active pet facts");
            Check.True(
                first.Species[0].DisplayName == species.DisplayName &&
                first.Aptitudes[0].DisplayName == aptitude.DisplayName,
                "an already pinned catalog never hot-reloads source changes");
        }
        finally
        {
            await ExecuteAsync(
                dataSource,
                "UPDATE pet_templates SET display_name = @name " +
                "WHERE species_id = @id;",
                ("name", sourceSpeciesName),
                ("id", species.SpeciesId));
            await ExecuteAsync(
                dataSource,
                "UPDATE pet_aptitude_templates SET display_name = @name " +
                "WHERE aptitude = @id;",
                ("name", sourceAptitudeName),
                ("id", aptitude.Aptitude));
        }
    }

    private static async Task AssertStableSourceIdentityCannotDisappearAsync(
        NpgsqlDataSource dataSource,
        short speciesId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand(
                "DELETE FROM pet_templates WHERE species_id = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("id", speciesId);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            await transaction.RollbackAsync();
            return;
        }

        await transaction.RollbackAsync();
        throw new InvalidOperationException(
            "Published pet species allowed its stable source identity to disappear.");
    }

    private static async Task AssertSealedRowsRejectMutationAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await AssertRejectedAsync(
            dataSource,
            """
            UPDATE pet_content_aptitude_definitions
            SET display_name = display_name || ' mutation'
            WHERE revision = @revision
              AND aptitude = (
                  SELECT min(aptitude)
                  FROM pet_content_aptitude_definitions
                  WHERE revision = @revision);
            """,
            revision,
            "sealed pet definitions reject updates");
        await AssertRejectedAsync(
            dataSource,
            """
            INSERT INTO pet_content_species_definitions
            SELECT *
            FROM pet_content_species_definitions
            WHERE revision = @revision
            ORDER BY species_id
            LIMIT 1;
            """,
            revision,
            "sealed pet revisions reject late inserts");
        await AssertRejectedAsync(
            dataSource,
            """
            DELETE FROM pet_content_publication
            WHERE family = 'pets';
            """,
            revision,
            "official pet publication rejects deletion");

        foreach (var (table, assertion) in GuardedPetContentTables.Where(
                     static value =>
                         value.Table == "pet_content_hatch_rank_steps" ||
                         value.Table == "pet_content_merge_savvy_lookup" ||
                         value.Table.StartsWith(
                             "pet_content_merge_rank_",
                             StringComparison.Ordinal)))
        {
            await AssertRejectedAsync(
                dataSource,
                $"""
                UPDATE public.{table}
                SET revision = revision
                WHERE revision = @revision;
                """,
                revision,
                $"sealed {assertion} reject updates");
        }
    }

    private static async Task AssertIncompletePublicationRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        var revision = NewRevision();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await InsertTestRevisionAsync(connection, transaction, revision);
            await using var command = new NpgsqlCommand(
                """
                UPDATE pet_content_publication
                SET revision = @revision,
                    published_at = now()
                WHERE family = 'pets';
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("revision", revision);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception) when (
            exception.MessageText.Contains(
                "is incomplete",
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync();
            return;
        }

        await transaction.RollbackAsync();
        throw new InvalidOperationException(
            "An incomplete pet revision became the official publication.");
    }

    private static async Task AssertRejectedAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string revision,
        string assertion)
    {
        try
        {
            await ExecuteAsync(dataSource, sql, ("revision", revision));
        }
        catch (PostgresException)
        {
            return;
        }

        throw new InvalidOperationException(assertion + " was accepted.");
    }

    private static async Task<T> ReadScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        AddParameters(command, parameters);
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException(
                "The pet-content integration query returned no value."));
    }

    private static async Task ExecuteAsync(
        NpgsqlDataSource dataSource,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameters(
        NpgsqlCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }
    }

    private static string NewRevision() =>
        Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}
