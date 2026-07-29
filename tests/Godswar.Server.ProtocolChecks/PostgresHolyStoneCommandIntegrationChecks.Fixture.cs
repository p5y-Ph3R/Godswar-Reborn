using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task<HolyFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        CompactItemEntry? target = null,
        HolyStoneTargetLocation targetLocation =
            HolyStoneTargetLocation.Equipment,
        short targetSlot = HolyStoneCommandEnvelope.WeaponEquipmentSlot,
        CompactItemEntry? stone = null,
        short stoneSlot = 10,
        IReadOnlyList<(short Slot, CompactItemEntry Item)>?
            additionalBagItems = null,
        bool fillBag = false,
        int gold = 1000)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09hly_{shortScenario}_{token}";
        var characterName = $"HS{shortScenario}{token}";
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

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
            account.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Holy Stone fixture account has no identity."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                name,
                camp,
                profession,
                fighter_job_lv,
                "Money",
                "Stone",
                wallet_revision,
                inventory_revision
            )
            VALUES (
                @accountId,
                @name,
                1,
                0,
                80,
                1000,
                @gold,
                0,
                0
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("name", characterName);
            character.Parameters.AddWithValue("gold", gold);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Holy Stone fixture character has no identity."));
        }

        long? targetId = null;
        if (target.HasValue)
        {
            targetId = await InsertItemAsync(
                connection,
                transaction,
                characterId,
                checked((short)targetLocation),
                targetSlot,
                target.Value);
        }
        long? stoneId = null;
        if (stone.HasValue)
        {
            stoneId = await InsertItemAsync(
                connection,
                transaction,
                characterId,
                1,
                stoneSlot,
                stone.Value);
        }
        if (additionalBagItems is not null)
        {
            foreach (var (slot, item) in additionalBagItems)
            {
                await InsertItemAsync(
                    connection,
                    transaction,
                    characterId,
                    1,
                    slot,
                    item);
            }
        }
        if (fillBag)
        {
            for (short slot = 0; slot < 96; slot++)
            {
                if (targetLocation == HolyStoneTargetLocation.KitBag &&
                    slot == targetSlot ||
                    stone.HasValue && slot == stoneSlot ||
                    additionalBagItems?.Any(
                        item => item.Slot == slot) == true)
                {
                    continue;
                }
                await InsertItemAsync(
                    connection,
                    transaction,
                    characterId,
                    1,
                    slot,
                    SimpleItem(9030));
            }
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                30,
                CancellationToken.None),
            "Holy Stone fixture captures an economy baseline");
        await transaction.CommitAsync();
        return new HolyFixture(
            accountId,
            characterId,
            new CommandSubject(accountId, characterId),
            targetLocation,
            targetSlot,
            stoneSlot,
            targetId,
            stoneId,
            target?.ToCompactString() ?? "[]",
            stone?.ToCompactString() ?? "[]");
    }

    private static async Task<long> InsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short location,
        short slot,
        CompactItemEntry item)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp,
                holy_suit_code, holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level
            )
            VALUES (
                @characterId, @location, @slot, @itemId,
                @quality, @grade, @bound, @stack, @itemExp,
                @holySuitCode, @socketCount,
                @socket1Effect, @socket1Level,
                @socket2Effect, @socket2Level,
                @socket3Effect, @socket3Level,
                @socket4Effect, @socket4Level
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("location", location);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)item.Id));
        command.Parameters.AddWithValue("quality", item.Quality);
        command.Parameters.AddWithValue("grade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        command.Parameters.AddWithValue("itemExp", item.Exp);
        command.Parameters.AddWithValue(
            "holySuitCode",
            item.HolySuitCode);
        command.Parameters.AddWithValue(
            "socketCount",
            item.SocketCount);
        AddNullable(command, "socket1Effect", item.Socket1EffectId);
        AddNullable(command, "socket1Level", item.Socket1Level);
        AddNullable(command, "socket2Effect", item.Socket2EffectId);
        AddNullable(command, "socket2Level", item.Socket2Level);
        AddNullable(command, "socket3Effect", item.Socket3EffectId);
        AddNullable(command, "socket3Level", item.Socket3Level);
        AddNullable(command, "socket4Effect", item.Socket4EffectId);
        AddNullable(command, "socket4Level", item.Socket4Level);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "The Holy Stone fixture item has no identity."));
    }

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        short? value) =>
        command.Parameters.Add(
            new NpgsqlParameter(name, NpgsqlDbType.Smallint)
            {
                Value = value.HasValue
                    ? value.Value
                    : DBNull.Value
            });

    private static CompactItemEntry Weapon(
        short sockets,
        short? effect1 = null,
        short? level1 = null,
        short? effect2 = null,
        short? level2 = null) =>
        CompactItemEntry.Empty with
        {
            Id = 1007,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1,
            SocketCount = sockets,
            Socket1EffectId = effect1,
            Socket1Level = level1,
            Socket2EffectId = effect2,
            Socket2Level = level2
        };

    private static CompactItemEntry SimpleItem(
        uint id,
        short grade = 1,
        short stack = 1) =>
        CompactItemEntry.Empty with
        {
            Id = id,
            Quality = 1,
            Grade = grade,
            Bound = 1,
            Stack = stack
        };

    private static async Task<HolyDurableState> ReadStateAsync(
        string connectionString,
        HolyFixture fixture,
        HolyStoneCommandOperation operation)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                cb.inventory_revision,
                cb.wallet_revision,
                cb."Stone",
                COALESCE((SELECT count(*) FROM public.command_audit
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0),
                COALESCE((SELECT count(*) FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0),
                COALESCE((SELECT count(*)
                 FROM public.character_inventory_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = @reasonCode), 0),
                COALESCE((SELECT count(*) FROM public.outbox_events
                 WHERE aggregate_key = @aggregateKey
                   AND event_type = @eventType), 0),
                COALESCE((SELECT max(duplicate_count)
                 FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0)::integer,
                COALESCE((SELECT max(request_conflict_count)
                 FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0)::integer,
                COALESCE((SELECT count(*)
                 FROM public.character_currency_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND currency_code = 'gold'
                   AND reason_code = @reasonCode), 0),
                COALESCE((SELECT sum(delta)
                 FROM public.character_currency_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND currency_code = 'gold'
                   AND reason_code = @reasonCode), 0)::bigint,
                COALESCE((SELECT is_reconciled
                 FROM public.character_wallet_reconciliation
                 WHERE character_id = @characterId), false),
                COALESCE((SELECT is_reconciled
                 FROM public.character_inventory_reconciliation
                 WHERE character_id = @characterId), false)
            FROM public.character_base cb
            WHERE cb.id = @characterId
              AND cb.account_id = @accountId;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            HolyStonePersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            HolyStonePersistenceCodec.CommandFamilyCode(operation));
        command.Parameters.AddWithValue(
            "reasonCode",
            HolyStonePersistenceCodec.LedgerReasonCode(operation));
        command.Parameters.AddWithValue(
            "eventType",
            HolyStonePersistenceCodec.EventType);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Holy Stone fixture disappeared.");
        }
        return new HolyDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetBoolean(11),
            reader.GetBoolean(12));
    }

    private static async Task<IReadOnlyList<HolyGoldLedgerEntry>>
        ReadGoldLedgerAsync(
            string connectionString,
            HolyFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                wallet_revision,
                delta,
                balance_before,
                balance_after,
                reason_code
            FROM public.character_currency_ledger
            WHERE account_id = @accountId
              AND character_id = @characterId
              AND currency_code = 'gold'
            ORDER BY wallet_revision, id;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        var entries = new List<HolyGoldLedgerEntry>();
        while (await reader.ReadAsync())
        {
            entries.Add(new HolyGoldLedgerEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4)));
        }
        return entries;
    }

    private static async Task<(long Id, CompactItemEntry Item)?>
        ReadItemAsync(
            string connectionString,
            int characterId,
            short location,
            short slot)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT ci.id, view.compact_entry
            FROM public.character_items ci
            JOIN public.character_item_compact_entries view
              ON view.user_id = ci.user_id
             AND view.item_location = ci.item_location
             AND view.slot_index = ci.slot_index
            WHERE ci.user_id = @characterId
              AND ci.item_location = @location
              AND ci.slot_index = @slot;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("location", location);
        command.Parameters.AddWithValue("slot", slot);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetInt64(0),
                CompactItemEntry.Parse(reader.GetString(1)))
            : null;
    }

    private sealed record HolyFixture(
        int AccountId,
        int CharacterId,
        CommandSubject Subject,
        HolyStoneTargetLocation TargetLocation,
        short TargetSlot,
        short StoneSlot,
        long? TargetItemId,
        long? StoneItemId,
        string TargetState,
        string StoneState);

    private sealed record HolyDurableState(
        long InventoryRevision,
        long WalletRevision,
        int Gold,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        long CurrencyLedgerCount,
        long GoldLedgerDelta,
        bool WalletReconciled,
        bool InventoryReconciled);

    private sealed record HolyGoldLedgerEntry(
        long WalletRevision,
        long Delta,
        long BalanceBefore,
        long BalanceAfter,
        string ReasonCode);
}
