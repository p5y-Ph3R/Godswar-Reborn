using Npgsql;

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
        ("pet_content_rebirth_steps", "rebirth steps")
    ];

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
                rebirth_step_count, source)
            VALUES (@revision, 1, 1, 1, 1, 1, 'declared-count-test');
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
}
