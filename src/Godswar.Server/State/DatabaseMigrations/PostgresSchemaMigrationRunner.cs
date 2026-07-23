using System.Diagnostics;
using Npgsql;

namespace Godswar.Server.State;

/// <summary>
/// Serializes schema initialization and applies each forward migration in its
/// own transaction. Existing databases never invoke the legacy bootstrap loader.
/// </summary>
internal sealed class PostgresSchemaMigrationRunner
{
    private const long AdvisoryLockKey = 0x475753525F4D4947L; // "GWSR_MIG"
    private const int ExpectedLegacyMarkerCount = 4;

    private const string InspectLegacySchemaSql = """
        SELECT count(*)::integer
        FROM unnest(ARRAY[
            'accounts',
            'character_base',
            'character_items',
            'item_templates'
        ]::text[]) AS expected(table_name)
        WHERE to_regclass('public.' || quote_ident(expected.table_name)) IS NOT NULL;
        """;

    private const string CreateHistoryTableSql = """
        CREATE TABLE IF NOT EXISTS public.schema_migrations (
            migration_id varchar(128) PRIMARY KEY,
            description varchar(255) NOT NULL,
            checksum char(64) NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            execution_ms bigint NOT NULL,
            CONSTRAINT ck_schema_migrations_checksum
                CHECK (checksum ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_schema_migrations_execution_ms
                CHECK (execution_ms >= 0)
        );
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresSchemaMigrationRunner(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task InitializeAsync(
        Func<CancellationToken, ValueTask<string>> loadLegacyBootstrapSql,
        IReadOnlyList<PostgresSchemaMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadLegacyBootstrapSql);
        ArgumentNullException.ThrowIfNull(migrations);

        // Validate ordering before opening a connection or taking a lock.
        _ = PostgresSchemaMigrationPlan.Build(
            migrations,
            Array.Empty<AppliedPostgresSchemaMigration>());

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var lockAcquired = false;
        try
        {
            await SetAdvisoryLockAsync(connection, acquire: true, cancellationToken);
            lockAcquired = true;

            var markerCount = await ReadLegacyMarkerCountAsync(connection, null, cancellationToken);
            var bootstrapDecision = ClassifyLegacySchema(markerCount);
            if (bootstrapDecision == LegacySchemaBootstrapDecision.BootstrapFreshDatabase)
            {
                var bootstrapSql = await loadLegacyBootstrapSql(cancellationToken);
                await BootstrapFreshDatabaseAsync(connection, bootstrapSql, cancellationToken);
            }

            await EnsureHistoryTableAsync(connection, cancellationToken);
            var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken);
            var pending = PostgresSchemaMigrationPlan.Build(migrations, applied);
            foreach (var migration in pending)
            {
                await ApplyMigrationAsync(connection, migration, cancellationToken);
            }
        }
        finally
        {
            if (lockAcquired)
            {
                await SetAdvisoryLockAsync(connection, acquire: false, CancellationToken.None);
            }
        }
    }

    internal static LegacySchemaBootstrapDecision ClassifyLegacySchema(int markerCount)
    {
        if (markerCount == 0)
        {
            return LegacySchemaBootstrapDecision.BootstrapFreshDatabase;
        }

        if (markerCount == ExpectedLegacyMarkerCount)
        {
            return LegacySchemaBootstrapDecision.BaselineExistingDatabase;
        }

        throw new InvalidOperationException(
            "PostgreSQL contains a partial Godswar schema " +
            $"({markerCount}/{ExpectedLegacyMarkerCount} core tables). " +
            "Refusing to replay the legacy bootstrap over an existing database.");
    }

    private static async Task BootstrapFreshDatabaseAsync(
        NpgsqlConnection connection,
        string bootstrapSql,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bootstrapSql))
        {
            throw new InvalidOperationException("The fresh-database bootstrap SQL is empty.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(bootstrapSql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var markerCount = await ReadLegacyMarkerCountAsync(connection, transaction, cancellationToken);
        if (markerCount != ExpectedLegacyMarkerCount)
        {
            throw new InvalidOperationException(
                "The legacy bootstrap did not create every required core table. " +
                "Its transaction will be rolled back.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureHistoryTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(CreateHistoryTableSql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<AppliedPostgresSchemaMigration>> ReadAppliedMigrationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT migration_id, checksum
            FROM public.schema_migrations
            ORDER BY migration_id;
            """;

        var applied = new List<AppliedPostgresSchemaMigration>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(new AppliedPostgresSchemaMigration(
                reader.GetString(0),
                reader.GetString(1)));
        }

        return applied;
    }

    private static async Task ApplyMigrationAsync(
        NpgsqlConnection connection,
        PostgresSchemaMigration migration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        await using (var command = new NpgsqlCommand(migration.Sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        stopwatch.Stop();
        const string insertSql = """
            INSERT INTO public.schema_migrations (
                migration_id,
                description,
                checksum,
                execution_ms
            )
            VALUES (@id, @description, @checksum, @executionMs);
            """;

        await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", migration.Id);
            command.Parameters.AddWithValue("description", migration.Description);
            command.Parameters.AddWithValue("checksum", migration.Checksum);
            command.Parameters.AddWithValue("executionMs", stopwatch.ElapsedMilliseconds);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine($"[db] applied schema migration {migration.Id}");
    }

    private static async Task<int> ReadLegacyMarkerCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            InspectLegacySchemaSql,
            connection,
            transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static async Task SetAdvisoryLockAsync(
        NpgsqlConnection connection,
        bool acquire,
        CancellationToken cancellationToken)
    {
        var function = acquire ? "pg_advisory_lock" : "pg_advisory_unlock";
        await using var command = new NpgsqlCommand(
            $"SELECT {function}(@lockKey);",
            connection);
        command.Parameters.AddWithValue("lockKey", AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal enum LegacySchemaBootstrapDecision
{
    BootstrapFreshDatabase,
    BaselineExistingDatabase
}
