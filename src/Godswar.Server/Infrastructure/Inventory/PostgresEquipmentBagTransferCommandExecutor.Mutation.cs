using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresEquipmentBagTransferCommandExecutor
{
    private async Task<EquipmentBagTransferExecutionResult>
        PersistTransferAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            TransferCommandContext context,
            LockedTransferSlots slots,
            EquipmentBagTransferResultStatus status,
            long inventoryRevision,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var source = status ==
                EquipmentBagTransferResultStatus.Equipped
            ? slots.KitBag
            : slots.Equipment;
        if (source is null)
        {
            throw new InvalidDataException(
                "The committed transfer has no source item.");
        }
        var nextRevision = checked(inventoryRevision + 1);
        var eventId = Guid.NewGuid();
        var equipmentState =
            slots.Equipment?.Item.ToCompactString() ?? "[]";
        var kitBagState =
            slots.KitBag?.Item.ToCompactString() ?? "[]";
        var evidence = await PersistResultEvidenceAsync(
            connection,
            transaction,
            context,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            status,
            equipmentState,
            kitBagState,
            nextRevision,
            eventId,
            cancellationToken);
        var afterState = await MoveItemAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            source,
            status,
            context.Command.EquipmentSlot,
            context.Command.KitBagSlot,
            cancellationToken);
        await RecomputeHolySuitPointsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            cancellationToken);
        await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            context.Subject,
            inventoryRevision,
            nextRevision,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentBagTransferCommandStage
                .InventoryRevisionAdvanced,
            0,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            evidence.InboxId,
            context.Subject,
            nextRevision,
            source,
            afterState,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentBagTransferCommandStage
                .InventoryLedgerInserted,
            0,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            evidence.InboxId,
            aggregateKey,
            nextRevision,
            eventId,
            evidence.Payload,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentBagTransferCommandStage.OutboxInserted,
            0,
            cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return EquipmentBagTransferExecutionResult
            .Committed(evidence.Receipt);
    }

    private async Task RecomputeHolySuitPointsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            "SELECT public.recompute_character_holy_suit_points(" +
            "@characterId);",
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not int)
        {
            throw new InvalidDataException(
                "The equipped Holy Suit points could not be recomputed.");
        }
    }

    private async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        long expectedRevision,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @nextRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND inventory_revision = @expectedRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("nextRevision", nextRevision);
        command.Parameters.AddWithValue(
            "expectedRevision",
            expectedRevision);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The inventory revision did not advance exactly once.");
        }
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandSubject subject,
        long inventoryRevision,
        LockedItem item,
        string afterState,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id,
                account_id,
                character_id,
                inventory_revision,
                entry_ordinal,
                item_instance_id,
                mutation_kind,
                state_contract_version,
                before_state,
                after_state,
                reason_code
            )
            VALUES (
                @inboxId,
                @accountId,
                @characterId,
                @inventoryRevision,
                0,
                @itemInstanceId,
                'move',
                1,
                @beforeState,
                @afterState,
                @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            item.ItemInstanceId);
        command.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value = item.BeforeState;
        command.Parameters.Add(
            "afterState",
            NpgsqlDbType.Jsonb).Value = afterState;
        command.Parameters.AddWithValue(
            "reasonCode",
            EquipmentBagTransferPersistenceCodec.LedgerReasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The equipment transfer ledger append was not exact.");
        }
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string aggregateKey,
        long inventoryRevision,
        Guid eventId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id,
                command_inbox_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                ordering_policy,
                payload,
                max_attempts
            )
            VALUES (
                @eventId,
                @inboxId,
                @consumerKey,
                @aggregateType,
                @aggregateKey,
                @aggregateVersion,
                @eventType,
                @contractVersion,
                @orderingPolicy,
                @payload,
                @maxAttempts
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            EquipmentBagTransferPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            EquipmentBagTransferPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "eventType",
            EquipmentBagTransferPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            EquipmentBagTransferPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            EquipmentBagTransferPersistenceCodec.OrderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The equipment transfer outbox insert was not exact.");
        }
    }
}
