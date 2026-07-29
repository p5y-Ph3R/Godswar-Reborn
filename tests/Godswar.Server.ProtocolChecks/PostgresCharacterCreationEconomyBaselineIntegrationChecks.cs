using System.Text.RegularExpressions;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class
    PostgresCharacterCreationEconomyBaselineIntegrationChecks
{
    internal const string CheckName =
        "PostgreSQL character-creation economy baseline";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b09_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(databaseName))
        {
            Console.WriteLine(
                $"SKIP {CheckName} requires a disposable " +
                "godswar_b03_*_smoke_XX or godswar_b09_* database; " +
                $"received '{databaseName}'");
            return;
        }

        await using var store =
            new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();

        var token = Guid.NewGuid().ToString("N")[..12];
        var account = await store.LoginOrCreateAccountAsync(
            $"b09_create_{token}",
            string.Empty);
        const int openingSilver = 123456;
        const int openingGold = 789;
        var character = await store.CreateCharacterAsync(
            account.Id,
            new GameCharacter
            {
                Name = $"B9Create{token}",
                Camp = GameDefaults.SpartaCamp,
                Profession = 0,
                Level = 80,
                Silver = openingSilver,
                Gold = openingGold
            });

        Check.Equal(
            openingSilver,
            character.Silver,
            "created character reloads its opening silver");
        Check.Equal(
            openingGold,
            character.Gold,
            "created character reloads its opening gold");

        var state = await ReadBaselineStateAsync(
            dataSource,
            account.Id,
            character.Id);
        Check.Equal(
            0L,
            state.CurrentWalletRevision,
            "new character starts at wallet revision zero");
        Check.Equal(
            0L,
            state.CurrentInventoryRevision,
            "new character starts at inventory revision zero");
        Check.Equal(
            0L,
            state.BaselineWalletRevision,
            "creation baseline captures wallet revision zero");
        Check.Equal(
            0L,
            state.BaselineInventoryRevision,
            "creation baseline captures inventory revision zero");
        Check.Equal(
            (long)openingSilver,
            state.BaselineSilver,
            "creation baseline captures exact opening silver");
        Check.Equal(
            (long)openingGold,
            state.BaselineGold,
            "creation baseline captures exact opening gold");
        Check.True(
            string.Equals(
                "character_creation",
                state.BaselineSource,
                StringComparison.Ordinal),
            "creation baseline records its authoritative provenance");
        Check.True(
            state.LiveItemCount > 0,
            "starter inventory exists before baseline capture");
        Check.Equal(
            state.LiveItemCount,
            state.BaselineItemCount,
            "baseline item count equals starter inventory");
        Check.Equal(
            state.LiveItemCount,
            state.SnapshotItemCount,
            "every starter item has one baseline snapshot");
        Check.Equal(
            state.LiveItemCount,
            state.ExactSnapshotItemCount,
            "starter item snapshots preserve exact row state");
        Check.True(
            state.BaselineCapturedAt ==
            state.EarliestItemCapturedAt &&
            state.BaselineCapturedAt ==
            state.LatestItemCapturedAt,
            "wallet and item baselines share the creation transaction timestamp");
        Check.Equal(
            0L,
            state.CurrencyLedgerCount,
            "opening wallet state is a baseline rather than a ledger mutation");
        Check.Equal(
            0L,
            state.InventoryLedgerCount,
            "opening items are a baseline rather than ledger mutations");
        Check.True(
            state.WalletReconciled,
            "new character wallet reconciles immediately");
        Check.True(
            state.InventoryReconciled,
            "new character inventory reconciles immediately");
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
               throw new InvalidDataException(
                   "PostgreSQL returned no current database name.");
    }

    private static async Task<CreationBaselineState>
        ReadBaselineStateAsync(
            NpgsqlDataSource dataSource,
            int accountId,
            int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                character_row.wallet_revision,
                character_row.inventory_revision,
                baseline_row.wallet_revision,
                baseline_row.inventory_revision,
                baseline_row.silver,
                baseline_row.gold,
                baseline_row.item_count,
                baseline_row.baseline_source,
                baseline_row.captured_at,
                (
                    SELECT count(*)::integer
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                ),
                (
                    SELECT count(*)::integer
                    FROM public.character_inventory_baseline_items
                    WHERE character_id = @characterId
                ),
                (
                    SELECT count(*)::integer
                    FROM public.character_inventory_baseline_items snapshot
                    JOIN public.character_items item_row
                      ON item_row.user_id = snapshot.character_id
                     AND item_row.id = snapshot.item_instance_id
                    WHERE snapshot.character_id = @characterId
                      AND snapshot.account_id = @accountId
                      AND snapshot.item_location =
                          item_row.item_location
                      AND snapshot.slot_index = item_row.slot_index
                      AND snapshot.prop_id = item_row.prop_id
                      AND snapshot.item_state = to_jsonb(item_row)
                ),
                (
                    SELECT min(captured_at)
                    FROM public.character_inventory_baseline_items
                    WHERE character_id = @characterId
                ),
                (
                    SELECT max(captured_at)
                    FROM public.character_inventory_baseline_items
                    WHERE character_id = @characterId
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_currency_ledger
                    WHERE character_id = @characterId
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                ),
                wallet_reconciliation.is_reconciled,
                inventory_reconciliation.is_reconciled
            FROM public.character_base character_row
            JOIN public.character_economy_baseline baseline_row
              ON baseline_row.character_id = character_row.id
             AND baseline_row.account_id = character_row.account_id
            JOIN public.character_wallet_reconciliation
                wallet_reconciliation
              ON wallet_reconciliation.character_id =
                 character_row.id
            JOIN public.character_inventory_reconciliation
                inventory_reconciliation
              ON inventory_reconciliation.character_id =
                 character_row.id
            WHERE character_row.id = @characterId
              AND character_row.account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Created character economy baseline was not found.");
        }

        return new CreationBaselineState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetDateTime(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetDateTime(12),
            reader.GetDateTime(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetBoolean(16),
            reader.GetBoolean(17));
    }

    private sealed record CreationBaselineState(
        long CurrentWalletRevision,
        long CurrentInventoryRevision,
        long BaselineWalletRevision,
        long BaselineInventoryRevision,
        long BaselineSilver,
        long BaselineGold,
        int BaselineItemCount,
        string BaselineSource,
        DateTime BaselineCapturedAt,
        int LiveItemCount,
        int SnapshotItemCount,
        int ExactSnapshotItemCount,
        DateTime EarliestItemCapturedAt,
        DateTime LatestItemCapturedAt,
        long CurrencyLedgerCount,
        long InventoryLedgerCount,
        bool WalletReconciled,
        bool InventoryReconciled);
}
