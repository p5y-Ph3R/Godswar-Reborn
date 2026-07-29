using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    private const short DefaultEquipmentSlot = 10;
    private const short DefaultKitBagSlot = 12;

    private static async Task<TransferFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        CompactItemEntry? equipmentItem = null,
        CompactItemEntry? kitBagItem = null,
        short equipmentSlot = DefaultEquipmentSlot,
        short kitBagSlot = DefaultKitBagSlot,
        short profession = 0,
        int characterLevel = 80,
        IReadOnlyList<(short Slot, CompactItemEntry Item)>?
            additionalEquipment = null)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09eqp_{shortScenario}_{token}";
        var characterName = $"EQ{shortScenario}{token}";
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
                    "The transfer fixture account has no identity."));
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
                @profession,
                @characterLevel,
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
            character.Parameters.AddWithValue("profession", profession);
            character.Parameters.AddWithValue(
                "characterLevel",
                characterLevel);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The transfer fixture character has no identity."));
        }

        long? equipmentId = null;
        long? kitBagId = null;
        if (equipmentItem.HasValue)
        {
            equipmentId = await InsertItemAsync(
                connection,
                transaction,
                characterId,
                location: 0,
                equipmentSlot,
                equipmentItem.Value);
        }
        if (kitBagItem.HasValue)
        {
            kitBagId = await InsertItemAsync(
                connection,
                transaction,
                characterId,
                location: 1,
                kitBagSlot,
                kitBagItem.Value);
        }
        if (additionalEquipment is not null)
        {
            foreach (var (slot, item) in additionalEquipment)
            {
                await InsertItemAsync(
                    connection,
                    transaction,
                    characterId,
                    location: 0,
                    slot,
                    item);
            }
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "equipment transfer fixture captures economy baseline");
        await transaction.CommitAsync();
        return new TransferFixture(
            username,
            accountId,
            characterId,
            new CommandSubject(accountId, characterId),
            equipmentSlot,
            kitBagSlot,
            equipmentId,
            kitBagId,
            equipmentItem?.ToCompactString() ?? "[]",
            kitBagItem?.ToCompactString() ?? "[]");
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
                holy_socket_count
            )
            VALUES (
                @characterId,
                @location,
                @slot,
                @itemId,
                @quality,
                @grade,
                @bound,
                @stack,
                @itemExp,
                @holySuitCode,
                @socketCount
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
        return Convert.ToInt64(
            await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "The transfer fixture item has no identity."));
    }

    private static async Task<TransferDurableState> ReadStateAsync(
        string connectionString,
        TransferFixture fixture)
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
                   AND item_location = 0
                   AND slot_index = @equipmentSlot), 0),
                COALESCE((SELECT id FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND slot_index = @kitBagSlot), 0),
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
                   AND source = 'client-equipment-bag-transfer'),
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
            "equipmentSlot",
            fixture.EquipmentSlot);
        command.Parameters.AddWithValue(
            "kitBagSlot",
            fixture.KitBagSlot);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            EquipmentBagTransferPersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            EquipmentBagTransferPersistenceCodec.CommandFamilyCode);
        command.Parameters.AddWithValue(
            "reasonCode",
            EquipmentBagTransferPersistenceCodec.LedgerReasonCode);
        command.Parameters.AddWithValue(
            "eventType",
            EquipmentBagTransferPersistenceCodec.EventType);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The equipment transfer fixture disappeared.");
        }
        return new TransferDurableState(
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
        short quality = 1,
        short grade = 1) =>
        CompactItemEntry.Parse(
            $"[{id},,,,,,{quality},{grade},1,1,0,0," +
            ",,,,,0,,,,,,,,,,,,]");

    private sealed record TransferFixture(
        string Username,
        int AccountId,
        int CharacterId,
        CommandSubject Subject,
        short EquipmentSlot,
        short KitBagSlot,
        long? EquipmentItemId,
        long? KitBagItemId,
        string EquipmentState,
        string KitBagState);

    private sealed record TransferDurableState(
        long InventoryRevision,
        long EquipmentItemId,
        long KitBagItemId,
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
