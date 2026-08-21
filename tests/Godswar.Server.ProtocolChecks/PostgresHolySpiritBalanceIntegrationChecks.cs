using System.Text.RegularExpressions;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresHolySpiritBalanceIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL mutable Holy Spirit balance authority";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b09|b12)_[a-z0-9_]{1,48}$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {CheckName} requires a disposable B09/B12 database; " +
                $"received '{database}'");
            return;
        }

        var fresh = await IsFreshAsync(dataSource);
        if (fresh)
        {
            var prefix = PostgresSchemaMigrationCatalog.All
                .Take(PostgresSchemaMigrationCatalog.All.Count - 1)
                .ToArray();
            var runner = new PostgresSchemaMigrationRunner(dataSource);
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                prefix);
            var fixture = await SeedOverCapSocketsAsync(dataSource);
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                PostgresSchemaMigrationCatalog.All);
            await AssertSocketsClampedAsync(dataSource, fixture);
        }
        else
        {
            await PostgresSchemaStartup.InitializeAsync(connectionString);
        }

        await AssertSnapshotAndCompareExchangeAsync(connectionString);
    }

    private static async Task AssertSnapshotAndCompareExchangeAsync(
        string connectionString)
    {
        var pinned = await PostgresHolySpiritBalanceSnapshotReader
            .LoadAsync(connectionString);
        Check.True(
            pinned.CooledPhysicalReductionGradeOneMaximum == 55 &&
            pinned.CooledMagicReductionGradeOneMaximum == 55 &&
            pinned.CooledCriticalReductionGradeOneMaximum == 60,
            "migration seeds the reviewed 5.5/5.5/6.0 balance");

        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        var fixture = await SeedOverCapSocketsAsync(dataSource);
        HolySpiritBalanceSnapshot? updated = null;
        try
        {
            var first = await PostgresHolySpiritBalanceStore.TryUpdateAsync(
                connectionString,
                new HolySpiritBalanceUpdate(
                    56,
                    57,
                    61,
                    pinned.Revision,
                    "protocol-check"));
            Check.True(
                first.Status == HolySpiritBalanceUpdateStatus.Updated &&
                first.Snapshot.Revision == pinned.Revision + 1 &&
                first.Snapshot.CooledPhysicalReductionGradeOneMaximum == 56 &&
                first.Snapshot.CooledMagicReductionGradeOneMaximum == 57 &&
                first.Snapshot.CooledCriticalReductionGradeOneMaximum == 61,
                "matching revision updates all balance fields atomically");
            updated = first.Snapshot;
            await AssertSocketValuesAsync(
                dataSource,
                fixture,
                [560, 560, 550, 560],
                [570, 570, 570, 550],
                [601, 610, 610, 599],
                "successful CAS clamps explicit and legacy NULL socket " +
                "values to its new maxima");
            await AssertFlatNullsUnchangedAsync(dataSource, fixture);

            var beforeStale = await ReadAllSocketValuesAsync(
                dataSource,
                fixture);

            var stale = await PostgresHolySpiritBalanceStore.TryUpdateAsync(
                connectionString,
                new HolySpiritBalanceUpdate(
                    58,
                    58,
                    62,
                    pinned.Revision,
                    "stale-protocol-check"));
            Check.True(
                stale.Status ==
                    HolySpiritBalanceUpdateStatus.RevisionConflict &&
                stale.Snapshot == updated,
                "stale management revision is rejected with current state");
            var afterStale = await ReadAllSocketValuesAsync(
                dataSource,
                fixture);
            Check.True(
                beforeStale.SequenceEqual(afterStale),
                "stale CAS does not mutate raw socket values");
            Check.True(
                pinned.CooledPhysicalReductionGradeOneMaximum == 55 &&
                pinned.CooledMagicReductionGradeOneMaximum == 55 &&
                pinned.CooledCriticalReductionGradeOneMaximum == 60,
                "an active worker snapshot does not hot-reload management edits");
        }
        finally
        {
            if (updated is not null)
            {
                var current = await PostgresHolySpiritBalanceSnapshotReader
                    .LoadAsync(connectionString);
                var restored =
                    await PostgresHolySpiritBalanceStore.TryUpdateAsync(
                        connectionString,
                        new HolySpiritBalanceUpdate(
                            pinned.CooledPhysicalReductionGradeOneMaximum,
                            pinned.CooledMagicReductionGradeOneMaximum,
                            pinned.CooledCriticalReductionGradeOneMaximum,
                            current.Revision,
                            "protocol-check-restore"));
                Check.True(
                    restored.Status ==
                        HolySpiritBalanceUpdateStatus.Updated,
                    "integration check restores the reviewed balance values");
                await AssertSocketValuesAsync(
                    dataSource,
                    fixture,
                    [550, 550, 550, 550],
                    [550, 550, 550, 550],
                    [600, 600, 600, 599],
                    "lowering the balance irreversibly clamps existing rolls");
            }
            await CleanupFixtureAsync(dataSource, fixture);
        }
    }

    private static async Task<BalanceSocketFixture>
        SeedOverCapSocketsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"holy_balance_{Guid.NewGuid():N}"[..32]);
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession,
                fighter_job_lv, "Money", "Stone")
            VALUES (@accountId, 1, @name, 1, 0, 80, 0, 0)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue(
                "name",
                $"HolyBalance{Guid.NewGuid():N}"[..32]);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }

        int templateId;
        await using (var template = new NpgsqlCommand(
            "SELECT id FROM public.item_templates ORDER BY id LIMIT 1;",
            connection,
            transaction))
        {
            templateId = Convert.ToInt32(
                await template.ExecuteScalarAsync());
        }

        var physical = await InsertSocketRowAsync(
            connection,
            transaction,
            characterId,
            templateId,
            slot: 0,
            effectId: 9,
            values: [null, 800, 550, null]);
        var magic = await InsertSocketRowAsync(
            connection,
            transaction,
            characterId,
            templateId,
            slot: 1,
            effectId: 10,
            values: [800, null, 799, 550]);
        var critical = await InsertSocketRowAsync(
            connection,
            transaction,
            characterId,
            templateId,
            slot: 2,
            effectId: 13,
            values: [601, 700, null, 599]);
        var flat = await InsertSocketRowAsync(
            connection,
            transaction,
            characterId,
            templateId,
            slot: 3,
            effectId: 11,
            values: [999, null, 999, null]);
        await transaction.CommitAsync();
        return new(
            accountId,
            characterId,
            physical,
            magic,
            critical,
            flat);
    }

    private static async Task<long> InsertSocketRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int templateId,
        short slot,
        short effectId,
        short?[] values)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level,
                holy_socket1_value, holy_socket2_value,
                holy_socket3_value, holy_socket4_value)
            VALUES (
                @characterId, 0, @slot, @templateId, 4,
                @effectId, 10, @effectId, 10,
                @effectId, 10, @effectId, 10,
                @value1, @value2, @value3, @value4)
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("templateId", templateId);
        command.Parameters.AddWithValue("effectId", effectId);
        for (var index = 0; index < values.Length; index++)
        {
            var parameter = command.Parameters.Add(
                $"value{index + 1}",
                NpgsqlDbType.Smallint);
            parameter.Value = values[index] is { } value
                ? value
                : DBNull.Value;
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task AssertSocketsClampedAsync(
        NpgsqlDataSource dataSource,
        BalanceSocketFixture fixture)
    {
        await AssertSocketValuesAsync(
            dataSource,
            fixture,
            [550, 550, 550, 550],
            [550, 550, 550, 550],
            [600, 600, 600, 599],
            "migration clamps explicit and legacy NULL percentage values");
        await AssertFlatNullsUnchangedAsync(dataSource, fixture);
    }

    private static async Task AssertFlatNullsUnchangedAsync(
        NpgsqlDataSource dataSource,
        BalanceSocketFixture fixture) =>
        Check.True(
            (await ReadSocketValuesAsync(dataSource, fixture.FlatId))
                .SequenceEqual(new short?[] { 999, null, 999, null }),
            "non-adjustable flat-effect NULLs remain unchanged");

    private static async Task AssertSocketValuesAsync(
        NpgsqlDataSource dataSource,
        BalanceSocketFixture fixture,
        short?[] physical,
        short?[] magic,
        short?[] critical,
        string context)
    {
        Check.True(
            (await ReadSocketValuesAsync(dataSource, fixture.PhysicalId))
                .SequenceEqual(physical) &&
            (await ReadSocketValuesAsync(dataSource, fixture.MagicId))
                .SequenceEqual(magic) &&
            (await ReadSocketValuesAsync(dataSource, fixture.CriticalId))
                .SequenceEqual(critical),
            context);
    }

    private static async Task<short?[]> ReadAllSocketValuesAsync(
        NpgsqlDataSource dataSource,
        BalanceSocketFixture fixture)
    {
        var values = new List<short?>(12);
        values.AddRange(await ReadSocketValuesAsync(
            dataSource,
            fixture.PhysicalId));
        values.AddRange(await ReadSocketValuesAsync(
            dataSource,
            fixture.MagicId));
        values.AddRange(await ReadSocketValuesAsync(
            dataSource,
            fixture.CriticalId));
        return values.ToArray();
    }

    private static async Task CleanupFixtureAsync(
        NpgsqlDataSource dataSource,
        BalanceSocketFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM public.accounts WHERE id = @accountId;");
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<short?[]> ReadSocketValuesAsync(
        NpgsqlDataSource dataSource,
        long itemId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT holy_socket1_value, holy_socket2_value,
                   holy_socket3_value, holy_socket4_value
            FROM public.character_items
            WHERE id = @itemId;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Holy Spirit balance fixture disappeared.");
        }
        return
        [
            reader.IsDBNull(0) ? null : reader.GetInt16(0),
            reader.IsDBNull(1) ? null : reader.GetInt16(1),
            reader.IsDBNull(2) ? null : reader.GetInt16(2),
            reader.IsDBNull(3) ? null : reader.GetInt16(3)
        ];
    }

    private static async Task<bool> IsFreshAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT to_regclass('public.schema_migrations') IS NULL;");
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private sealed record BalanceSocketFixture(
        int AccountId,
        int CharacterId,
        long PhysicalId,
        long MagicId,
        long CriticalId,
        long FlatId);
}
