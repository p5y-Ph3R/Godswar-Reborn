using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresKitBagItemMoveCommandExecutor
{
    private async Task<KitBagItemMoveExecutionResult>
        PersistMovementAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            KitBagItemMoveCommandContext context,
            LockedKitBagSlots slots,
            KitBagItemMoveResultStatus status,
            long inventoryRevision,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var source = slots.Source ??
            throw new InvalidDataException(
                "The committed move has no source item.");
        var nextRevision = checked(inventoryRevision + 1);
        var eventId = Guid.NewGuid();
        var sourceState = source.Item.ToCompactString();
        var destinationState =
            slots.Destination?.Item.ToCompactString() ?? "[]";
        var evidence = await PersistResultEvidenceAsync(
            connection,
            transaction,
            context,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            status,
            sourceState,
            destinationState,
            nextRevision,
            eventId,
            cancellationToken);

        var after = await MoveItemsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            slots,
            context.Command.SourceKitBagSlot,
            context.Command.DestinationKitBagSlot,
            cancellationToken);
        await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            context.Subject,
            inventoryRevision,
            nextRevision,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage
                .InventoryRevisionAdvanced,
            0,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            evidence.InboxId,
            context.Subject,
            nextRevision,
            entryOrdinal: 0,
            source,
            after.SourceAfterState,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage
                .InventoryLedgerInserted,
            0,
            cancellationToken);
        if (slots.Destination is not null)
        {
            if (after.DestinationAfterState is null)
            {
                throw new InvalidDataException(
                    "A swap has no destination final state.");
            }
            await InsertInventoryLedgerAsync(
                connection,
                transaction,
                evidence.InboxId,
                context.Subject,
                nextRevision,
                entryOrdinal: 1,
                slots.Destination,
                after.DestinationAfterState,
                cancellationToken);
            await ReachAsync(
                PostgresKitBagItemMoveCommandStage
                    .InventoryLedgerInserted,
                1,
                cancellationToken);
        }

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
            PostgresKitBagItemMoveCommandStage.OutboxInserted,
            0,
            cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return KitBagItemMoveExecutionResult.Committed(
            evidence.Receipt);
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
        short entryOrdinal,
        LockedKitBagItem item,
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
                @entryOrdinal,
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
            "entryOrdinal",
            entryOrdinal);
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
            KitBagItemMovePersistenceCodec.LedgerReasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The kit-bag move ledger append was not exact.");
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
            KitBagItemMovePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            KitBagItemMovePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "eventType",
            KitBagItemMovePersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            KitBagItemMovePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            KitBagItemMovePersistenceCodec.OrderingPolicy);
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
                "The kit-bag move outbox insert was not exact.");
        }
    }
}
