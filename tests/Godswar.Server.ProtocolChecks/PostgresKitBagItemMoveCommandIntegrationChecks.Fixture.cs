using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemMoveCommandIntegrationChecks
{
    private const short DefaultSourceSlot = 12;
    private const short DefaultDestinationSlot = 13;

    private static async Task<MoveFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        bool sourcePresent = true,
        bool destinationPresent = false,
        CompactItemEntry? sourceItem = null,
        CompactItemEntry? destinationItem = null)
    {
        sourceItem ??= Item(4212, 2);
        destinationItem ??= Item(4213, 1, quality: 3);
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09mov_{shortScenario}_{token}";
        var characterName = $"KM{shortScenario}{token}";
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
                    "The move fixture account has no identity."));
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
                100,
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
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The move fixture character has no identity."));
        }

        long? sourceId = null;
        long? destinationId = null;
        if (sourcePresent)
        {
            sourceId = await InsertItemAsync(
                connection,
                transaction,
                characterId,
                DefaultSourceSlot,
                sourceItem.Value);
        }
        if (destinationPresent)
        {
            destinationId = await InsertItemAsync(
                connection,
                transaction,
                characterId,
                DefaultDestinationSlot,
                destinationItem.Value);
        }
        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "item-move fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new MoveFixture(
            username,
            accountId,
            characterId,
            new CommandSubject(accountId, characterId),
            DefaultSourceSlot,
            DefaultDestinationSlot,
            sourceId,
            destinationId,
            sourcePresent
                ? sourceItem.Value.ToCompactString()
                : "[]",
            destinationPresent
                ? destinationItem.Value.ToCompactString()
                : "[]");
    }

    private static async Task<long> InsertItemAsync(
        string connectionString,
        MoveFixture fixture,
        short slot,
        CompactItemEntry item)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var id = await InsertItemAsync(
            connection,
            transaction,
            fixture.CharacterId,
            slot,
            item);
        await transaction.CommitAsync();
        return id;
    }

    private static async Task<long> InsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short slot,
        CompactItemEntry item)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp,
                holy_suit_code,
                holy_socket_count,
                holy_socket1_effect_id,
                holy_socket1_level,
                holy_socket1_value
            )
            VALUES (
                @characterId,
                1,
                @slot,
                @itemId,
                @quality,
                @grade,
                @bound,
                @stack,
                @itemExp,
                @holySuitCode,
                @socketCount,
                @socket1EffectId,
                @socket1Level,
                @socket1Value
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
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
        AddNullable(command, "socket1EffectId", item.Socket1EffectId);
        AddNullable(command, "socket1Level", item.Socket1Level);
        AddNullable(command, "socket1Value", item.Socket1Value);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "The move fixture item has no identity."));
    }

    private static async Task<MoveDurableState> ReadStateAsync(
        string connectionString,
        MoveFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
                COALESCE((SELECT id FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND slot_index = @sourceSlot), 0),
                COALESCE((SELECT id FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND slot_index = @destinationSlot), 0),
                (SELECT count(*) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 2),
                (SELECT count(*) FROM public.command_audit
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.character_item_audit
                 WHERE user_id = @characterId
                   AND source = 'client-bag-move'
                   AND action = 'move'),
                (SELECT count(*) FROM public.character_inventory_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = @reasonCode),
                (SELECT count(*) FROM public.outbox_events
                 WHERE aggregate_key = @aggregateKey
                   AND event_type = @eventType),
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
                COALESCE((SELECT is_reconciled
                 FROM public.character_inventory_reconciliation
                 WHERE character_id = @characterId), false)
            FROM public.character_base character_row
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "sourceSlot",
            fixture.SourceSlot);
        command.Parameters.AddWithValue(
            "destinationSlot",
            fixture.DestinationSlot);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            KitBagItemMovePersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            KitBagItemMovePersistenceCodec.CommandFamilyCode);
        command.Parameters.AddWithValue(
            "reasonCode",
            KitBagItemMovePersistenceCodec.LedgerReasonCode);
        command.Parameters.AddWithValue(
            "eventType",
            KitBagItemMovePersistenceCodec.EventType);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The item-move fixture disappeared.");
        }
        return new MoveDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetBoolean(11));
    }

    private static CompactItemEntry Item(
        uint id,
        short stack,
        short quality = 1,
        short grade = 1) =>
        CompactItemEntry.Parse(
            $"[{id},,,,,,{quality},{grade},1,{stack},0,0," +
            ",,,,,0,,,,,,,,,,,,]");

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        short? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Smallint).Value =
            value.HasValue ? value.Value : DBNull.Value;

    private sealed record MoveFixture(
        string Username,
        int AccountId,
        int CharacterId,
        CommandSubject Subject,
        short SourceSlot,
        short DestinationSlot,
        long? SourceItemId,
        long? DestinationItemId,
        string SourceState,
        string DestinationState);

    private sealed record MoveDurableState(
        long InventoryRevision,
        long SourceItemId,
        long DestinationItemId,
        long TemporaryItemCount,
        long AuditCount,
        long InboxCount,
        long CompatibilityAuditCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        bool IsReconciled);
}
