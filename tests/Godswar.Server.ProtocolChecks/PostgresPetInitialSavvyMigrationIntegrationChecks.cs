using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetInitialSavvyMigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId =
        "20260728_019_pet_initial_savvy_policy";
    private const string RequiredMigrationId =
        "20260728_018_pet_growth_policy_v2";
    private const string ArchiveRelation =
        "public.pet_initial_savvy_reconciliation_archive";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet initial-savvy migration integration ({ConnectionStringVariable} is not set)");
            return;
        }

        if (!await IsMigrationAppliedAsync(
                connectionString,
                RequiredMigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet initial-savvy migration integration ({RequiredMigrationId} is required)");
            return;
        }

        if (await IsMigrationAppliedAsync(connectionString, MigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet initial-savvy migration integration ({MigrationId} is already applied)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var migrationRunner =
            new PostgresSchemaMigrationRunner(dataSource);
        await migrationRunner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All.Take(19).ToArray());

        Check.True(
            await IsMigrationAppliedAsync(
                connectionString,
                RequiredMigrationId),
            "integration database has growth-v2 applied");
        Check.True(
            !await IsMigrationAppliedAsync(
                connectionString,
                MigrationId),
            "integration setup leaves initial-savvy migration unapplied");
        Check.True(
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "integration database has no partial initial-savvy archive");

        var token = Guid.NewGuid().ToString("N")[..10];
        var username = $"pet_savvy_{token}";
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
            var before = await ReadBeforeSnapshotAsync(
                connection,
                transaction,
                fixture);

            var migration = PostgresSchemaMigrationCatalog.All.Single(
                candidate => candidate.Id == MigrationId);
            await ExecuteAsync(
                connection,
                transaction,
                migration.Sql);

            await CheckSchemaAsync(connection, transaction);
            await CheckZeroPetAsync(
                connection,
                transaction,
                fixture,
                before);
            await CheckProgressedPetAsync(
                connection,
                transaction,
                fixture,
                before);
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
            "transaction rollback removes the initial-savvy archive");
        Check.Equal(
            0L,
            await CountFixtureAccountsAsync(
                connectionString,
                username),
            "transaction rollback removes initial-savvy fixtures");
        Check.True(
            !await IsMigrationAppliedAsync(
                connectionString,
                MigrationId),
            "rollback leaves initial-savvy migration unapplied");
    }

    private static async Task<BeforeSnapshot> ReadBeforeSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Fixture fixture)
    {
        var zeroPetStable = await ReadPetJsonAsync(
            connection,
            transaction,
            fixture.ZeroPetId,
            removeRevisionMetadata: true);
        var zeroStatsStable = await ReadStatsJsonAsync(
            connection,
            transaction,
            fixture.ZeroPetId,
            removeSavvyAndRevision: true);
        var progressedPet = await ReadPetJsonAsync(
            connection,
            transaction,
            fixture.ProgressedPetId,
            removeRevisionMetadata: false);
        var progressedStats = await ReadStatsJsonAsync(
            connection,
            transaction,
            fixture.ProgressedPetId,
            removeSavvyAndRevision: false);

        return new BeforeSnapshot(
            zeroPetStable,
            zeroStatsStable,
            progressedPet,
            progressedStats);
    }

    private static async Task CheckZeroPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Fixture fixture,
        BeforeSnapshot before)
    {
        Check.Equal(
            before.ZeroPetStableJson,
            await ReadPetJsonAsync(
                connection,
                transaction,
                fixture.ZeroPetId,
                removeRevisionMetadata: true),
            "zero-savvy reconciliation preserves every other pet field");
        Check.Equal(
            before.ZeroStatsStableJson,
            await ReadStatsJsonAsync(
                connection,
                transaction,
                fixture.ZeroPetId,
                removeSavvyAndRevision: true),
            "zero-savvy reconciliation preserves every other stat field");

        await using var petCommand = new NpgsqlCommand(
            """
            SELECT
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                revision,
                updated_at
            FROM public.character_pets
            WHERE id = @petId;
            """,
            connection,
            transaction);
        petCommand.Parameters.AddWithValue("petId", fixture.ZeroPetId);
        await using (var reader = await petCommand.ExecuteReaderAsync())
        {
            Check.True(await reader.ReadAsync(), "zero-savvy pet remains");
            Check.Equal(775, reader.GetInt32(0), "Rational midpoint baseline");
            Check.Equal(
                PetInitialSavvyPolicy.Version,
                reader.GetString(1),
                "zero-savvy pet records policy provenance");
            Check.Equal(102L, reader.GetInt64(2), "pet revision advances once");
            Check.True(
                reader.GetDateTime(3) >
                new DateTime(2026, 7, 20, 4, 5, 6, DateTimeKind.Utc),
                "pet revision timestamp advances");
        }

        await using var statCommand = new NpgsqlCommand(
            """
            SELECT
                stat_code,
                initial_savvy,
                added_savvy,
                growth_acceleration,
                revision,
                base_growth_rate
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code;
            """,
            connection,
            transaction);
        statCommand.Parameters.AddWithValue("petId", fixture.ZeroPetId);
        var rows = 0;
        await using (var reader = await statCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var statCode = reader.GetInt16(0);
                Check.Equal(
                    statCode <= 4 ? 129.17m : 129.16m,
                    reader.GetDecimal(1),
                    "midpoint centipoints distribute deterministically");
                Check.Equal(
                    100m + statCode + 0.25m,
                    reader.GetDecimal(2),
                    "added savvy is unchanged");
                Check.Equal(
                    200m + statCode + 0.50m,
                    reader.GetDecimal(3),
                    "growth acceleration is unchanged");
                Check.Equal(
                    1001L + statCode,
                    reader.GetInt64(4),
                    "stat revision advances once");
                Check.Equal(
                    0.20m + statCode * 0.01m,
                    reader.GetDecimal(5),
                    "base growth is unchanged");
                rows++;
            }
        }

        Check.Equal(6, rows, "zero-savvy pet retains six stat rows");
    }

    private static async Task CheckProgressedPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Fixture fixture,
        BeforeSnapshot before)
    {
        Check.Equal(
            before.ProgressedPetJson,
            await ReadPetJsonAsync(
                connection,
                transaction,
                fixture.ProgressedPetId,
                removeRevisionMetadata: false),
            "progressed Transcendent pet preserves every pet field");
        Check.Equal(
            before.ProgressedStatsJson,
            await ReadStatsJsonAsync(
                connection,
                transaction,
                fixture.ProgressedPetId,
                removeSavvyAndRevision: false),
            "progressed Transcendent pet preserves every stat field");

        await using var command = new NpgsqlCommand(
            """
            SELECT
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                (
                    SELECT sum(stat.initial_savvy)
                    FROM public.character_pet_stat_values stat
                    WHERE stat.pet_id = pet.id
                )
            FROM public.character_pets pet
            WHERE pet.id = @petId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "petId",
            fixture.ProgressedPetId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "progressed pet remains");
        Check.True(
            reader.IsDBNull(0) && reader.IsDBNull(1),
            "progressed pet receives no invented baseline provenance");
        Check.Equal(
            5_610m,
            reader.GetDecimal(2),
            "progressed total remains above the Transcendent hatch ceiling");
        Check.True(
            reader.GetDecimal(2) >
            PetInitialSavvyPolicy.All[^1].MaximumTotalSavvy,
            "migration preserves legitimate post-hatch progression");
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
                old_initial_savvy,
                old_revision,
                old_pet_revision,
                old_initial_savvy_baseline_total,
                old_initial_savvy_policy_version,
                archived_at
            FROM public.pet_initial_savvy_reconciliation_archive
            WHERE pet_id_snapshot IN (@zeroPetId, @progressedPetId)
            ORDER BY pet_id_snapshot, stat_code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("zeroPetId", fixture.ZeroPetId);
        command.Parameters.AddWithValue(
            "progressedPetId",
            fixture.ProgressedPetId);

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Check.Equal(MigrationId, reader.GetString(0), "archive migration");
            Check.Equal(
                fixture.ZeroPetId,
                reader.GetInt64(1),
                "only the complete zero-savvy pet is archived");
            Check.Equal(fixture.OwnerId, reader.GetInt32(2), "archive owner");
            Check.Equal(
                (short)PetAptitude.Rational,
                reader.GetInt16(3),
                "archive aptitude");
            var statCode = reader.GetInt16(4);
            Check.Equal(0m, reader.GetDecimal(5), "archive old savvy");
            Check.Equal(
                1000L + statCode,
                reader.GetInt64(6),
                "archive old revision");
            Check.Equal(
                101L,
                reader.GetInt64(7),
                "archive snapshots the old parent-pet revision");
            Check.True(
                reader.IsDBNull(8) && reader.IsDBNull(9),
                "archive retains absent baseline provenance");
            Check.True(!reader.IsDBNull(10), "archive timestamp");
            rows++;
        }

        Check.Equal(6, rows, "exactly six zero-savvy rows are archived");
    }

    private static Task<string> ReadPetJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        bool removeRevisionMetadata) =>
        ScalarAsync<string>(
            connection,
            transaction,
            removeRevisionMetadata
                ? """
                  SELECT (
                      to_jsonb(pet)
                      - 'initial_savvy_baseline_total'
                      - 'initial_savvy_policy_version'
                      - 'revision'
                      - 'updated_at'
                  )::text
                  FROM public.character_pets pet
                  WHERE pet.id = @petId;
                  """
                : """
                  SELECT (
                      to_jsonb(pet)
                      - 'initial_savvy_baseline_total'
                      - 'initial_savvy_policy_version'
                  )::text
                  FROM public.character_pets pet
                  WHERE pet.id = @petId;
                  """,
            ("petId", petId));

    private static Task<string> ReadStatsJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        bool removeSavvyAndRevision) =>
        ScalarAsync<string>(
            connection,
            transaction,
            removeSavvyAndRevision
                ? """
                  SELECT jsonb_agg(
                      to_jsonb(stat)
                      - 'initial_savvy'
                      - 'revision'
                      ORDER BY stat.stat_code
                  )::text
                  FROM public.character_pet_stat_values stat
                  WHERE stat.pet_id = @petId;
                  """
                : """
                  SELECT jsonb_agg(
                      to_jsonb(stat)
                      ORDER BY stat.stat_code
                  )::text
                  FROM public.character_pet_stat_values stat
                  WHERE stat.pet_id = @petId;
                  """,
            ("petId", petId));

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
        command.Parameters.AddWithValue("migrationId", migrationId);
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
        command.Parameters.AddWithValue("qualifiedName", qualifiedName);
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
        long ZeroPetId,
        long ProgressedPetId);

    private sealed record BeforeSnapshot(
        string ZeroPetStableJson,
        string ZeroStatsStableJson,
        string ProgressedPetJson,
        string ProgressedStatsJson);
}
