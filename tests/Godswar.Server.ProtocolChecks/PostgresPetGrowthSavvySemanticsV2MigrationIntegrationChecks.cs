using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetGrowthSavvySemanticsV2MigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string RequiredMigrationId =
        "20260810_068_pet_point_reset_dialogue";
    private const string MigrationId =
        "20260810_069_pet_growth_savvy_semantics_v2";
    private const string ArchiveRelation =
        "public.pet_growth_savvy_semantics_v2_archive";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet growth/Savvy v2 integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        if (await IsMigrationAppliedAsync(connectionString, MigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet growth/Savvy v2 integration " +
                $"({MigrationId} is already applied)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var migrationIndex = PostgresSchemaMigrationCatalog.All
            .Select((migration, index) => (migration, index))
            .Single(entry => entry.migration.Id == MigrationId)
            .index;
        var migrationRunner = new PostgresSchemaMigrationRunner(dataSource);
        await migrationRunner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All
                .Take(migrationIndex)
                .ToArray());

        Check.True(
            await IsMigrationAppliedAsync(
                connectionString,
                RequiredMigrationId),
            "integration setup applies migration 068");
        Check.True(
            !await IsMigrationAppliedAsync(connectionString, MigrationId),
            "integration setup leaves migration 069 unapplied");
        Check.True(
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "integration database has no partial migration-069 archive");

        var fixtureToken = Guid.NewGuid().ToString("N")[..10];
        var fixtureName = $"M069Pet{fixtureToken}";
        var fixtureUsername = $"m069_pet_{fixtureToken}";
        var fixtureOwnerName = $"M069{fixtureToken}";
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            var petId = await InsertLegacyFixtureAsync(
                connection,
                transaction,
                fixtureName,
                fixtureUsername,
                fixtureOwnerName);
            var migration = PostgresSchemaMigrationCatalog.All.Single(
                candidate => candidate.Id == MigrationId);

            await ExecuteAsync(
                connection,
                transaction,
                migration.Sql);

            await AssertMigratedPetAsync(
                connection,
                transaction,
                petId);
            await AssertArchiveAsync(
                connection,
                transaction,
                petId);
            await AssertConstraintsAsync(
                connection,
                transaction,
                petId);
            await RecordMigrationAsync(
                connection,
                transaction,
                migration);
            await AssertCompletedCatalogIsIdempotentAsync(
                connection,
                transaction);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Check.True(
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "rollback removes the migration-069 archive");
        Check.True(
            !await IsMigrationAppliedAsync(connectionString, MigrationId),
            "rollback leaves migration 069 unapplied");
        Check.Equal(
            0L,
            await CountFixturePetsAsync(connectionString, fixtureName),
            "rollback removes the migration-069 fixture");
    }

    private static async Task AssertMigratedPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using (var parent = new NpgsqlCommand(
            """
            SELECT
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                revision
            FROM public.character_pets
            WHERE id = @petId;
            """,
            connection,
            transaction))
        {
            parent.Parameters.AddWithValue("petId", petId);
            await using var reader = await parent.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync(),
                "migration-069 fixture pet remains present");
            Check.Equal(879, reader.GetInt32(0), "hatch Savvy total becomes Basic metadata");
            Check.Equal("project-v2", reader.GetString(1), "Basic Savvy records its actual source policy");
            Check.Equal(879, reader.GetInt32(2), "legacy rarity total remains a compatibility mirror");
            Check.Equal("project-v2", reader.GetString(3), "legacy rarity policy remains a compatibility mirror");
            Check.Equal("savvy-plus-growth-v2", reader.GetString(4), "corrected semantics provenance is explicit");
            Check.Equal(8L, reader.GetInt64(5), "pet revision advances exactly once");
        }

        var stats = await ReadStatsAsync(
            connection,
            transaction,
            petId);
        Check.Equal(6, stats.Count, "corrected pet retains six stat rows");
        Check.True(
            stats.Select(static stat => stat.BirthSavvy).Distinct().Count() == 1,
            "equal six-way hatch Savvy is valid");

        var expectedGrowth = new[]
        {
            0.38m, 0.41m, 0.40m, 0.47m, 0.44m, 0.45m
        };
        for (var index = 0; index < stats.Count; index++)
        {
            var stat = stats[index];
            var basicProgress = index == 0 ? 7m : 0m;
            var addedProgress = index == 0 ? 5m : 0m;
            Check.Equal(
                146.50m + basicProgress,
                stat.InitialSavvy,
                $"stat {index + 1} preserves accumulated Basic progression");
            Check.Equal(
                expectedGrowth[index] + addedProgress,
                stat.AddedValue,
                $"stat {index + 1} preserves accumulated Added progression");
            Check.Equal(
                expectedGrowth[index],
                stat.BaseGrowth,
                $"stat {index + 1} preserves base Growth");
            Check.Equal(
                index == 0 ? 0.20m : 0m,
                stat.GrowthAcceleration,
                $"stat {index + 1} preserves rebirth Growth acceleration");
            Check.Equal(146.50m, stat.BirthSavvy, $"stat {index + 1} receives hatch Savvy baseline");
            Check.Equal(146.50m, stat.LegacyRaritySavvy, $"stat {index + 1} retains compatibility baseline");
            Check.Equal(12L, stat.Revision, $"stat {index + 1} revision advances exactly once");
        }
    }

    private static async Task AssertArchiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                count(*),
                count(DISTINCT old_rarity_added_savvy),
                min(old_pet_revision),
                max(old_pet_revision),
                min(old_stat_revision),
                max(old_stat_revision),
                max(old_initial_savvy) FILTER (WHERE stat_code = 1),
                max(old_added_savvy) FILTER (WHERE stat_code = 1)
            FROM public.pet_growth_savvy_semantics_v2_archive
            WHERE migration_id = @migrationId
              AND pet_id_snapshot = @petId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "migration archive is queryable");
        Check.Equal(6L, reader.GetInt64(0), "archive has one before-image per stat");
        Check.Equal(1L, reader.GetInt64(1), "archive permits equal six-way Savvy");
        Check.Equal(7L, reader.GetInt64(2), "archive preserves minimum old pet revision");
        Check.Equal(7L, reader.GetInt64(3), "archive preserves maximum old pet revision");
        Check.Equal(11L, reader.GetInt64(4), "archive preserves minimum old stat revision");
        Check.Equal(11L, reader.GetInt64(5), "archive preserves maximum old stat revision");
        Check.Equal(7.38m, reader.GetDecimal(6), "archive preserves progressed old Basic value");
        Check.Equal(151.50m, reader.GetDecimal(7), "archive preserves progressed old Added value");
    }

    private static async Task AssertConstraintsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        var validated = await ScalarAsync<long>(
            connection,
            transaction,
            """
            SELECT count(*)
            FROM pg_constraint
            WHERE convalidated
              AND conname IN (
                  'ck_character_pets_savvy_provenance',
                  'ck_pet_stat_savvy_birth_baseline',
                  'ck_pet_stat_savvy_progression',
                  'ck_pet_stat_added_value_progression'
              );
            """);
        Check.Equal(4L, validated, "all corrected semantics constraints are validated");

        var obsolete = await ScalarAsync<long>(
            connection,
            transaction,
            """
            SELECT count(*)
            FROM pg_constraint
            WHERE conname IN (
                'ck_pet_stat_growth_x1_birth_baseline',
                'ck_pet_stat_initial_savvy_progression',
                'ck_pet_stat_added_savvy_progression'
            );
            """);
        Check.Equal(0L, obsolete, "obsolete reversed-semantics constraints are removed");

        await AssertRejectedAsync(
            connection,
            transaction,
            "UPDATE character_pet_stat_values SET birth_initial_savvy = rarity_added_savvy + 0.01 WHERE pet_id = @petId AND stat_code = 1;",
            petId,
            "birth Basic Savvy cannot diverge from its hatch baseline");
        await AssertRejectedAsync(
            connection,
            transaction,
            "UPDATE character_pet_stat_values SET initial_savvy = birth_initial_savvy - 0.01 WHERE pet_id = @petId AND stat_code = 1;",
            petId,
            "current Basic Savvy cannot fall below its hatch baseline");
        await AssertRejectedAsync(
            connection,
            transaction,
            "UPDATE character_pet_stat_values SET added_savvy = base_growth_rate - 0.01 WHERE pet_id = @petId AND stat_code = 1;",
            petId,
            "Added Value cannot fall below base Growth");
        await AssertRejectedAsync(
            connection,
            transaction,
            "UPDATE character_pets SET initial_savvy_policy_version = NULL WHERE id = @petId;",
            petId,
            "corrected pet provenance cannot become partial");
    }

    private static async Task RecordMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresSchemaMigration migration) =>
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO public.schema_migrations (
                migration_id,
                description,
                checksum,
                execution_ms
            )
            VALUES (@id, @description, @checksum, 0);
            """,
            ("id", migration.Id),
            ("description", migration.Description),
            ("checksum", migration.Checksum));

    private static async Task AssertCompletedCatalogIsIdempotentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var applied = new List<AppliedPostgresSchemaMigration>();
        await using var command = new NpgsqlCommand(
            """
            SELECT migration_id, checksum
            FROM public.schema_migrations
            ORDER BY migration_id;
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            applied.Add(
                new AppliedPostgresSchemaMigration(
                    reader.GetString(0),
                    reader.GetString(1)));
        }

        Check.Equal(
            0,
            PostgresSchemaMigrationPlan.Build(
                PostgresSchemaMigrationCatalog.All,
                applied).Count,
            "recorded migration leaves no pending catalog work");
        Check.Equal(
            0,
            PostgresSchemaMigrationPlan.Build(
                PostgresSchemaMigrationCatalog.All,
                applied).Count,
            "repeated completed-catalog planning is idempotent");
    }

    private static async Task<IReadOnlyList<MigratedStat>> ReadStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                stat_code,
                initial_savvy,
                added_savvy,
                base_growth_rate,
                growth_acceleration,
                birth_initial_savvy,
                rarity_added_savvy,
                revision
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        var values = new List<MigratedStat>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(
                new MigratedStat(
                    reader.GetInt16(0),
                    reader.GetDecimal(1),
                    reader.GetDecimal(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6),
                    reader.GetInt64(7)));
        }

        return values;
    }

    private static async Task AssertRejectedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        long petId,
        string description)
    {
        const string savepoint = "pet_growth_savvy_v2_rejection";
        await ExecuteAsync(connection, transaction, $"SAVEPOINT {savepoint};");
        var rejected = false;
        try
        {
            await ExecuteAsync(
                connection,
                transaction,
                sql,
                ("petId", petId));
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            rejected = true;
        }
        finally
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"ROLLBACK TO SAVEPOINT {savepoint};");
            await ExecuteAsync(
                connection,
                transaction,
                $"RELEASE SAVEPOINT {savepoint};");
        }

        Check.True(rejected, description);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        return (T)Convert.ChangeType(
            await command.ExecuteScalarAsync() ??
                throw new InvalidOperationException("Expected a scalar value."),
            typeof(T));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

}
