using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetGrowthV2MigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId =
        "20260728_018_pet_growth_policy_v2";
    private const string RequiredMigrationId =
        "20260728_017_pet_growth_midpoint_backfill";
    private const string ArchiveRelation =
        "public.pet_growth_reconciliation_archive";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-growth v2 integration ({ConnectionStringVariable} is not set)");
            return;
        }

        if (!await IsMigrationAppliedAsync(
                connectionString,
                RequiredMigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-growth v2 integration ({RequiredMigrationId} is required)");
            return;
        }

        if (await IsMigrationAppliedAsync(
                connectionString,
                MigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-growth v2 integration ({MigrationId} is already applied)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var migrationRunner =
            new PostgresSchemaMigrationRunner(dataSource);
        await migrationRunner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All.Take(18).ToArray());

        Check.True(
            !await IsMigrationAppliedAsync(
                connectionString,
                MigrationId),
            "integration setup leaves pet-growth v2 unapplied");
        Check.True(
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "integration database has no partial pet-growth v2 archive");

        var token = Guid.NewGuid().ToString("N")[..10];
        var username = $"pet_v2_{token}";
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            var fixture = await InsertFixturesAsync(
                connection,
                transaction,
                token,
                username);
            var migration = PostgresSchemaMigrationCatalog.All.Single(
                candidate => candidate.Id == MigrationId);
            await ExecuteAsync(
                connection,
                transaction,
                migration.Sql);

            await CheckStatRowsAsync(
                connection,
                transaction,
                fixture);
            await CheckArchiveAsync(
                connection,
                transaction,
                fixture);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Check.True(
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "transaction rollback removes the integration archive");
        Check.Equal(
            0L,
            await CountFixtureAccountsAsync(
                connectionString,
                username),
            "transaction rollback removes pet-growth integration fixtures");
    }

    private static async Task<Fixture> InsertFixturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string token,
        string username)
    {
        var accountId = await ScalarAsync<int>(
            connection,
            transaction,
            """
            INSERT INTO public.accounts (username)
            VALUES (@username)
            RETURNING id;
            """,
            ("username", username));
        var ownerId = await ScalarAsync<int>(
            connection,
            transaction,
            """
            INSERT INTO public.character_base (account_id, name)
            VALUES (@accountId, @name)
            RETURNING id;
            """,
            ("accountId", accountId),
            ("name", $"PetV2{token}"));
        var inBracketPetId = await InsertPetAsync(
            connection,
            transaction,
            ownerId,
            $"Inside{token}",
            aptitude: 14);
        var outOfBracketPetId = await InsertPetAsync(
            connection,
            transaction,
            ownerId,
            $"Outside{token}",
            aptitude: 1);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id,
                stat_code,
                initial_savvy,
                added_savvy,
                growth_acceleration,
                revision,
                base_growth_rate
            )
            SELECT
                @inBracketPetId,
                stat_code,
                10 + stat_code,
                20 + stat_code,
                30 + stat_code,
                100 + stat_code,
                CASE
                    WHEN stat_code <= 2 THEN 8.59
                    ELSE 8.58
                END
            FROM generate_series(1, 6) AS stat(stat_code)
            UNION ALL
            SELECT
                @outOfBracketPetId,
                stat_code,
                40 + stat_code,
                50 + stat_code,
                60 + stat_code,
                200 + stat_code,
                CASE
                    WHEN stat_code <= 4 THEN 0.42
                    ELSE 0.41
                END
            FROM generate_series(1, 6) AS stat(stat_code);
            """,
            ("inBracketPetId", inBracketPetId),
            ("outOfBracketPetId", outOfBracketPetId));

        return new Fixture(
            ownerId,
            inBracketPetId,
            outOfBracketPetId);
    }

    private static Task<long> InsertPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int ownerId,
        string name,
        short aptitude) =>
        ScalarAsync<long>(
            connection,
            transaction,
            """
            INSERT INTO public.character_pets (
                user_id,
                species_id,
                name,
                sex,
                aptitude
            )
            VALUES (@ownerId, 1, @name, 0, @aptitude)
            RETURNING id;
            """,
            ("ownerId", ownerId),
            ("name", name),
            ("aptitude", aptitude));

    private static async Task CheckStatRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Fixture fixture)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                pet_id,
                stat_code,
                initial_savvy,
                added_savvy,
                growth_acceleration,
                revision,
                base_growth_rate
            FROM public.character_pet_stat_values
            WHERE pet_id IN (@inBracketPetId, @outOfBracketPetId)
            ORDER BY pet_id, stat_code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "inBracketPetId",
            fixture.InBracketPetId);
        command.Parameters.AddWithValue(
            "outOfBracketPetId",
            fixture.OutOfBracketPetId);

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var petId = reader.GetInt64(0);
            var statCode = reader.GetInt16(1);
            var isInBracket =
                petId == fixture.InBracketPetId;
            Check.True(
                isInBracket ||
                petId == fixture.OutOfBracketPetId,
                "v2 integration reads only fixture pets");

            var expectedOffset = isInBracket ? 0 : 30;
            Check.Equal(
                10m + statCode + expectedOffset,
                reader.GetDecimal(2),
                "v2 reconciliation preserves initial savvy");
            Check.Equal(
                20m + statCode + expectedOffset,
                reader.GetDecimal(3),
                "v2 reconciliation preserves added savvy");
            Check.Equal(
                30m + statCode + expectedOffset,
                reader.GetDecimal(4),
                "v2 reconciliation preserves growth acceleration");

            if (isInBracket)
            {
                Check.Equal(
                    100L + statCode,
                    reader.GetInt64(5),
                    "in-bracket revision remains unchanged");
                Check.Equal(
                    statCode <= 2 ? 8.59m : 8.58m,
                    reader.GetDecimal(6),
                    "in-bracket growth remains unchanged");
            }
            else
            {
                Check.Equal(
                    201L + statCode,
                    reader.GetInt64(5),
                    "out-of-bracket revision advances exactly once");
                Check.Equal(
                    0.010000m,
                    reader.GetDecimal(6),
                    "out-of-bracket growth uses the exact v2 midpoint split");
            }

            rows++;
        }

        Check.Equal(
            12,
            rows,
            "v2 reconciliation retains six stats for both fixture pets");
    }

    private static async Task CheckArchiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Fixture fixture)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                migration_id,
                pet_id_snapshot,
                owner_user_id_snapshot,
                aptitude_snapshot,
                stat_code,
                old_base_growth_rate,
                old_revision,
                archived_at
            FROM public.pet_growth_reconciliation_archive
            WHERE pet_id_snapshot IN (
                @inBracketPetId,
                @outOfBracketPetId
            )
            ORDER BY pet_id_snapshot, stat_code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "inBracketPetId",
            fixture.InBracketPetId);
        command.Parameters.AddWithValue(
            "outOfBracketPetId",
            fixture.OutOfBracketPetId);

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Check.Equal(
                MigrationId,
                reader.GetString(0),
                "archive identifies the reconciling migration");
            Check.Equal(
                fixture.OutOfBracketPetId,
                reader.GetInt64(1),
                "only the out-of-bracket fixture is archived");
            Check.Equal(
                fixture.OwnerId,
                reader.GetInt32(2),
                "archive snapshots the owner");
            Check.Equal(
                1,
                (int)reader.GetInt16(3),
                "archive snapshots the aptitude");
            var statCode = reader.GetInt16(4);
            Check.Equal(
                statCode <= 4 ? 0.42m : 0.41m,
                reader.GetDecimal(5),
                "archive retains old growth");
            Check.Equal(
                200L + statCode,
                reader.GetInt64(6),
                "archive retains old revision");
            Check.True(
                !reader.IsDBNull(7),
                "archive records a timestamp");
            rows++;
        }

        Check.Equal(
            6,
            rows,
            "exactly six out-of-bracket before-images are archived");
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }

        return (T)(await command.ExecuteScalarAsync()
                   ?? throw new InvalidOperationException(
                       "PostgreSQL fixture command returned null."));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString,
        string migrationId)
    {
        if (!await RelationExistsAsync(
                connectionString,
                "public.schema_migrations"))
        {
            return false;
        }

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.schema_migrations
                WHERE migration_id = @migrationId
            );
            """,
            connection);
        command.Parameters.AddWithValue(
            "migrationId",
            migrationId);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Migration-presence check returned null."));
    }

    private static async Task<bool> RelationExistsAsync(
        string connectionString,
        string qualifiedName)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualifiedName) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue(
            "qualifiedName",
            qualifiedName);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Relation-presence check returned null."));
    }

    private static async Task<long> CountFixtureAccountsAsync(
        string connectionString,
        string username)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM public.accounts
            WHERE username = @username;
            """,
            connection);
        command.Parameters.AddWithValue("username", username);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Fixture-cleanup check returned null."));
    }

    private sealed record Fixture(
        int OwnerId,
        long InBracketPetId,
        long OutOfBracketPetId);
}
