using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresMigrationPrefixFixtureChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string PrefixVariable =
        "GODSWAR_TEST_POSTGRES_MIGRATION_PREFIX";

    public static async Task RunAsync()
    {
        var requestedPrefix = Environment.GetEnvironmentVariable(PrefixVariable);
        if (string.IsNullOrWhiteSpace(requestedPrefix))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL migration-prefix fixture " +
                $"({PrefixVariable} is not set)");
            return;
        }

        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{PrefixVariable} requires {ConnectionStringVariable}.");
        }

        var targetIndex = PostgresSchemaMigrationCatalog.All
            .Select(static (migration, index) => (migration, index))
            .SingleOrDefault(candidate =>
                string.Equals(
                    candidate.migration.Id,
                    requestedPrefix,
                    StringComparison.Ordinal))
            .index;
        if (targetIndex == 0 &&
            !string.Equals(
                PostgresSchemaMigrationCatalog.All[0].Id,
                requestedPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unknown PostgreSQL migration prefix '{requestedPrefix}'.");
        }

        var expected = PostgresSchemaMigrationCatalog.All
            .Take(targetIndex + 1)
            .ToArray();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var runner = new PostgresSchemaMigrationRunner(dataSource);
        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            expected);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT migration_id, checksum
            FROM public.schema_migrations
            ORDER BY migration_id;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var actualIndex = 0;
        while (await reader.ReadAsync())
        {
            Check.True(
                actualIndex < expected.Length,
                "migration-prefix fixture has no unregistered suffix");
            Check.Equal(
                expected[actualIndex].Id,
                reader.GetString(0),
                $"migration-prefix row {actualIndex} ID");
            Check.Equal(
                expected[actualIndex].Checksum,
                reader.GetString(1),
                $"migration-prefix row {actualIndex} checksum");
            actualIndex++;
        }

        Check.Equal(
            expected.Length,
            actualIndex,
            "migration-prefix fixture has the exact requested history");
        Console.WriteLine(
            $"PostgreSQL migration-prefix fixture ready at {requestedPrefix}.");
    }
}
