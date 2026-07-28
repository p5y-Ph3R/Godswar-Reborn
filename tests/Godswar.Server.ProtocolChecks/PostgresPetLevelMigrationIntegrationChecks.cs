using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLevelMigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId =
        "20260729_022_pet_level_progression";
    private const string RequiredMigrationId =
        "20260729_021_pet_savvy_semantics_hardening";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-level migration integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        if (!await IsMigrationAppliedAsync(
                connectionString,
                RequiredMigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-level migration integration " +
                $"({RequiredMigrationId} is required)");
            return;
        }

        if (await IsMigrationAppliedAsync(
                connectionString,
                MigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-level migration integration " +
                $"({MigrationId} is already applied)");
            return;
        }

        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id == MigrationId);
        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"pet_lvl_mig_{token}";
        var before = await ReadDatabaseStateAsync(connectionString);

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            var fixture = await InsertFixtureAsync(
                connection,
                transaction,
                token,
                username);
            await ExecuteAsync(
                connection,
                transaction,
                migration.Sql);

            await AssertOperationConstraintAsync(
                connection,
                transaction,
                fixture);
            await AssertOpcodeMetadataAsync(
                connection,
                transaction);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Check.Equal(
            before,
            await ReadDatabaseStateAsync(connectionString),
            "rollback restores the pre-022 constraint and opcode state");
        Check.Equal(
            0L,
            await CountFixtureAccountsAsync(
                connectionString,
                username),
            "rollback removes every pet-level migration fixture");
        Check.True(
            !await IsMigrationAppliedAsync(
                connectionString,
                MigrationId),
            "rollback leaves migration 022 unapplied");
    }
}
