using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task PersistInventoryMutationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        long inboxId,
        IReadOnlyList<InventoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (mutations.Count is < 1 or > 2 ||
            mutations.Any(mutation =>
                mutation.InventoryRevision !=
                    mutations[0].InventoryRevision))
        {
            throw new InvalidDataException(
                "A pet bag activation has invalid inventory evidence.");
        }
        for (var ordinal = 0; ordinal < mutations.Count; ordinal++)
        {
            await InsertInventoryLedgerAsync(
                connection,
                transaction,
                subject,
                inboxId,
                checked((short)ordinal),
                mutations[ordinal],
                cancellationToken);
        }

        var eventId = Guid.NewGuid();
        var payload =
            PetBagActivationInventoryPersistenceCodec.Encode(
                new PetBagActivationInventoryReceipt(
                    subject.CharacterId,
                    mutations[0].InventoryRevision,
                    mutations.Count,
                    eventId));
        await using var outbox = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id, command_inbox_id, consumer_key,
                aggregate_type, aggregate_key, aggregate_version,
                event_type, contract_version, ordering_policy,
                payload, max_attempts
            )
            VALUES (
                @eventId, @inboxId, @consumerKey,
                @aggregateType, @aggregateKey, @aggregateVersion,
                @eventType, @contractVersion, @orderingPolicy,
                @payload, @maxAttempts
            );
            """,
            connection,
            transaction);
        outbox.Parameters.AddWithValue("eventId", eventId);
        outbox.Parameters.AddWithValue("inboxId", inboxId);
        outbox.Parameters.AddWithValue(
            "consumerKey",
            PetBagActivationInventoryPersistenceCodec.ConsumerKey);
        outbox.Parameters.AddWithValue(
            "aggregateType",
            PetBagActivationInventoryPersistenceCodec.AggregateType);
        outbox.Parameters.AddWithValue(
            "aggregateKey",
            PetBagActivationInventoryPersistenceCodec.AggregateKey(
                subject.CharacterId));
        outbox.Parameters.AddWithValue(
            "aggregateVersion",
            mutations[0].InventoryRevision);
        outbox.Parameters.AddWithValue(
            "eventType",
            PetBagActivationInventoryPersistenceCodec.EventType);
        outbox.Parameters.AddWithValue(
            "contractVersion",
            PetBagActivationInventoryPersistenceCodec.ContractVersion);
        outbox.Parameters.AddWithValue(
            "orderingPolicy",
            PetBagActivationInventoryPersistenceCodec.OrderingPolicy);
        outbox.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value = Encoding.UTF8.GetString(payload);
        outbox.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await outbox.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet bag inventory outbox append was not exact.");
        }
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        long inboxId,
        short entryOrdinal,
        InventoryMutation mutation,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id, account_id, character_id,
                inventory_revision, entry_ordinal, item_instance_id,
                mutation_kind, state_contract_version,
                before_state, after_state, reason_code
            )
            VALUES (
                @inboxId, @accountId, @characterId,
                @inventoryRevision, @entryOrdinal, @itemInstanceId,
                @mutationKind, 1,
                @beforeState, @afterState, @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            mutation.InventoryRevision);
        command.Parameters.AddWithValue(
            "entryOrdinal",
            entryOrdinal);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            mutation.ItemInstanceId);
        command.Parameters.AddWithValue(
            "mutationKind",
            mutation.MutationKind);
        command.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value = mutation.BeforeState;
        command.Parameters.Add(
            "afterState",
            NpgsqlDbType.Jsonb).Value =
            mutation.AfterState is null
                ? DBNull.Value
                : mutation.AfterState;
        command.Parameters.AddWithValue(
            "reasonCode",
            mutation.ReasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet bag inventory ledger append was not exact.");
        }
    }
}
