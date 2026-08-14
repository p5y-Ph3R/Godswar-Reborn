using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetInitialSavvyV3MigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId =
        "20260811_070_pet_initial_savvy_policy_v3";
    private const string ArchiveRelation =
        "public.pet_initial_savvy_v3_legacy_archive";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet initial-Savvy V3 integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        if (await IsMigrationAppliedAsync(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet initial-Savvy V3 integration " +
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
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "integration database has no partial migration-070 archive");

        var token = Guid.NewGuid().ToString("N")[..10];
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            var fixtures = await InsertFixturesAsync(
                connection,
                transaction,
                token);
            var beforeStats = new Dictionary<long, string>();
            foreach (var fixture in fixtures)
            {
                beforeStats[fixture.PetId] = await ReadStatJsonAsync(
                    connection,
                    transaction,
                    fixture.PetId);
            }

            var migration = PostgresSchemaMigrationCatalog.All.Single(
                candidate => candidate.Id == MigrationId);
            await ExecuteAsync(connection, transaction, migration.Sql);

            await AssertParentsAsync(
                connection,
                transaction,
                fixtures);
            await AssertStatsAndArchiveAsync(
                connection,
                transaction,
                fixtures,
                beforeStats);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Check.True(
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "rollback removes the migration-070 archive");
        Check.Equal(
            0L,
            await CountFixturesAsync(connectionString, token),
            "rollback removes migration-070 fixtures");
    }

    private static async Task<IReadOnlyList<Fixture>> InsertFixturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string token)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH created_account AS (
                INSERT INTO public.accounts (username)
                VALUES (@username)
                RETURNING id
            ),
            created_owner AS (
                INSERT INTO public.character_base (account_id, name)
                SELECT id, @ownerName
                FROM created_account
                RETURNING id
            )
            INSERT INTO public.character_pets (
                user_id, species_id, name, sex, level, aptitude,
                remaining_lifetime,
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                revision
            )
            SELECT
                owner.id, 1, @petPrefix || policy.ordinality::text,
                0, 2, 6, 600, 879,
                policy.version, 879, policy.version,
                'savvy-plus-growth-v2', 7
            FROM created_owner owner
            CROSS JOIN unnest(ARRAY['project-v1', 'project-v2'])
                WITH ORDINALITY AS policy(version, ordinality)
            RETURNING id, initial_savvy_policy_version;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("username", $"m070_{token}");
        command.Parameters.AddWithValue("ownerName", $"M070{token}");
        command.Parameters.AddWithValue("petPrefix", $"M070Pet{token}");
        var fixtures = new List<Fixture>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                fixtures.Add(
                    new Fixture(reader.GetInt64(0), reader.GetString(1)));
            }
        }

        Check.Equal(2, fixtures.Count, "both historical policy labels are represented");
        foreach (var fixture in fixtures)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO public.character_pet_stat_values (
                    pet_id, stat_code, initial_savvy, added_savvy,
                    base_growth_rate, growth_acceleration,
                    birth_initial_savvy, rarity_added_savvy, revision
                )
                SELECT
                    @petId,
                    value.stat_code,
                    146.50 + CASE WHEN value.stat_code = 1 THEN 7 ELSE 0 END,
                    value.growth +
                        CASE WHEN value.stat_code = 1 THEN 5 ELSE 0 END,
                    value.growth,
                    CASE WHEN value.stat_code = 1 THEN 0.20 ELSE 0 END,
                    146.50,
                    146.50,
                    11
                FROM (VALUES
                    (1::smallint, 0.38::numeric),
                    (2::smallint, 0.41::numeric),
                    (3::smallint, 0.40::numeric),
                    (4::smallint, 0.47::numeric),
                    (5::smallint, 0.44::numeric),
                    (6::smallint, 0.45::numeric)
                ) value(stat_code, growth);
                """,
                ("petId", fixture.PetId));
        }

        return fixtures;
    }

    private static async Task AssertParentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Fixture> fixtures)
    {
        foreach (var fixture in fixtures)
        {
            await using var command = new NpgsqlCommand(
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
                transaction);
            command.Parameters.AddWithValue("petId", fixture.PetId);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync(), "legacy pet remains present");
            Check.Equal(879, reader.GetInt32(0), "Basic Savvy baseline is unchanged");
            Check.Equal(
                PetSavvyRuntimeSemantics.LegacyHighSavvyPolicyVersion,
                reader.GetString(1),
                "Basic Savvy receives the explicit legacy-range label");
            Check.Equal(879, reader.GetInt32(2), "compatibility baseline is unchanged");
            Check.Equal(
                PetSavvyRuntimeSemantics.LegacyHighSavvyPolicyVersion,
                reader.GetString(3),
                "compatibility policy receives the legacy-range label");
            Check.Equal(
                PetSavvyRuntimeSemantics.SourceVersion,
                reader.GetString(4),
                "field-semantics provenance is unchanged");
            Check.Equal(8L, reader.GetInt64(5), "parent revision advances exactly once");
        }
    }

    private static async Task AssertStatsAndArchiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Fixture> fixtures,
        IReadOnlyDictionary<long, string> beforeStats)
    {
        foreach (var fixture in fixtures)
        {
            Check.Equal(
                beforeStats[fixture.PetId],
                await ReadStatJsonAsync(
                    connection,
                    transaction,
                    fixture.PetId),
                "migration preserves every six-stat field byte-for-byte");

            await using var command = new NpgsqlCommand(
                """
                SELECT
                    old_initial_savvy_policy_version,
                    old_rarity_savvy_policy_version,
                    old_pet_revision,
                    old_stat_rows::text
                FROM public.pet_initial_savvy_v3_legacy_archive
                WHERE migration_id = @migrationId
                  AND pet_id_snapshot = @petId;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("migrationId", MigrationId);
            command.Parameters.AddWithValue("petId", fixture.PetId);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync(), "one parent before-image is archived");
            Check.Equal(fixture.Policy, reader.GetString(0), "old Basic policy is archived");
            Check.Equal(fixture.Policy, reader.GetString(1), "old mirror policy is archived");
            Check.Equal(7L, reader.GetInt64(2), "old parent revision is archived");
            Check.Equal(beforeStats[fixture.PetId], reader.GetString(3), "six stat rows are archived exactly");
        }
    }

    private static async Task<string> ReadStatJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT jsonb_agg(to_jsonb(stat) ORDER BY stat.stat_code)::text
            FROM public.character_pet_stat_values stat
            WHERE stat.pet_id = @petId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        return Convert.ToString(await command.ExecuteScalarAsync()) ??
            throw new InvalidOperationException("Expected six pet stat rows.");
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var relation = new NpgsqlCommand(
            "SELECT to_regclass('public.schema_migrations') IS NOT NULL;",
            connection))
        {
            if (!Convert.ToBoolean(await relation.ExecuteScalarAsync()))
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.schema_migrations " +
            "WHERE migration_id = @migrationId);",
            connection);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> RelationExistsAsync(
        string connectionString,
        string relation)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@relation) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue("relation", relation);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountFixturesAsync(
        string connectionString,
        string token)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM public.character_pets WHERE name LIKE @name;",
            connection);
        command.Parameters.AddWithValue("name", $"M070Pet{token}%");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed record Fixture(long PetId, string Policy);
}
