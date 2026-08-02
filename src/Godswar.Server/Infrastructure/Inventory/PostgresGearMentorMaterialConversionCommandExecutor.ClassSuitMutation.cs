using System.Globalization;
using System.Text;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresGearMentorMaterialConversionCommandExecutor
{
    private async Task<ClassSuitExecutionResult>
        PersistClassSuitCommitAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            ClassSuitCommandContext context,
            long currentInventoryRevision,
            ClassSuitPlan plan,
            LockedKitBag bag,
            LockedInventoryItem? equipment,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        ValidateClassSuitPlan(
            context.Command,
            plan,
            bag,
            equipment);
        var inventoryRevision = checked(currentInventoryRevision + 1);
        var eventId = Guid.NewGuid();
        var auditId = await InsertClassSuitAuditAsync(
            connection,
            transaction,
            context,
            ClassSuitCommandResultStatus.Succeeded,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receiptMutations = plan.Mutations.Select(static mutation =>
            new ClassSuitReceiptMutation(
                mutation.Slot,
                mutation.Before.Id,
                mutation.After.Id,
                mutation.Before.ToCompactString(),
                mutation.After.ToCompactString(),
                mutation.Location)).ToArray();
        var receipt = new ClassSuitExecutionReceipt(
            context.Family,
            context.Subject.CharacterId,
            context.Command.Operation,
            context.Command.NpcId,
            context.Command.DialogIndex,
            context.ReplayIntent,
            ClassSuitCommandResultStatus.Succeeded,
            ClassSuitNativeResults.Resolve(
                context.Command.Operation,
                ClassSuitCommandResultStatus.Succeeded),
            receiptMutations,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload = ClassSuitPersistenceCodec.Encode(receipt);
        var inboxId = await InsertClassSuitInboxAsync(
            connection,
            transaction,
            context.Family,
            ClassSuitCommandResultStatus.Succeeded,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            auditId,
            payload,
            cancellationToken);

        var databaseMutations = await ApplyClassSuitMutationsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            plan.Mutations,
            bag,
            equipment,
            cancellationToken);
        await AdvanceClassSuitInventoryRevisionAsync(
            connection,
            transaction,
            context,
            currentInventoryRevision,
            inventoryRevision,
            cancellationToken);
        await InsertClassSuitLedgerAsync(
            connection,
            transaction,
            inboxId,
            context,
            inventoryRevision,
            databaseMutations,
            cancellationToken);
        await InsertClassSuitOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            inventoryRevision,
            eventId,
            payload,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ClassSuitExecutionResult.Committed(receipt);
    }

    private static void ValidateClassSuitPlan(
        ClassSuitCommand command,
        ClassSuitPlan plan,
        LockedKitBag bag,
        LockedInventoryItem? equipment)
    {
        if (!plan.Committed ||
            plan.Mutations.Count is < 1 or
                > ClassSuitPersistenceCodec.MaximumMutationCount ||
            plan.Mutations.Select(static value =>
                    (value.Location, value.Slot))
                .Distinct().Count() != plan.Mutations.Count ||
            !plan.Mutations.Any(value =>
                value.Location == command.Gear.Location &&
                value.Slot == command.Gear.KitBagSlot))
        {
            throw new InvalidDataException(
                "The committed Class Suit plan has invalid mutation evidence.");
        }

        foreach (var mutation in plan.Mutations)
        {
            var locked = mutation.Location ==
                ClassSuitItemLocation.Equipment
                ? equipment
                : bag.Items.GetValueOrDefault(
                    checked((short)mutation.Slot));
            var actual = locked?.Item ?? CompactItemEntry.Empty;
            if (locked is not null &&
                (locked.ItemLocation != (short)mutation.Location ||
                 locked.Slot != mutation.Slot) ||
                actual != mutation.Before ||
                mutation.Before == mutation.After)
            {
                throw new InvalidDataException(
                    "The locked inventory differs from the Class Suit plan.");
            }
        }
    }

    private async Task<IReadOnlyList<InventoryMutation>>
        ApplyClassSuitMutationsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            IReadOnlyList<ClassSuitPlannedMutation> plan,
            LockedKitBag bag,
            LockedInventoryItem? equipment,
            CancellationToken cancellationToken)
    {
        var mutations = new List<InventoryMutation>(plan.Count);
        foreach (var mutation in plan
                     .OrderBy(static value => value.Location)
                     .ThenBy(static value => value.Slot))
        {
            var locked = mutation.Location ==
                ClassSuitItemLocation.Equipment
                ? equipment
                : bag.Items.GetValueOrDefault(
                    checked((short)mutation.Slot));
            if (mutation.Location == ClassSuitItemLocation.Equipment)
            {
                if (locked is null ||
                    mutation.Before.IsEmpty ||
                    mutation.After.IsEmpty)
                {
                    throw new InvalidDataException(
                        "The Class Suit equipped-item update is inconsistent.");
                }
                mutations.Add(await UpdateItemAsync(
                    connection,
                    transaction,
                    characterId,
                    locked,
                    mutation.After,
                    cancellationToken));
                continue;
            }
            if (mutation.Before.IsEmpty)
            {
                if (mutation.After.IsEmpty || locked is not null)
                {
                    throw new InvalidDataException(
                        "The Class Suit insert plan is inconsistent.");
                }
                mutations.Add(await InsertItemAsync(
                    connection,
                    transaction,
                    characterId,
                    checked((short)mutation.Slot),
                    mutation.After,
                    cancellationToken));
            }
            else if (mutation.After.IsEmpty)
            {
                mutations.Add(await DeleteItemAsync(
                    connection,
                    transaction,
                    characterId,
                    locked ?? throw new InvalidDataException(
                        "The Class Suit delete source is missing."),
                    cancellationToken));
            }
            else
            {
                mutations.Add(await UpdateItemAsync(
                    connection,
                    transaction,
                    characterId,
                    locked ?? throw new InvalidDataException(
                        "The Class Suit update source is missing."),
                    mutation.After,
                    cancellationToken));
            }
        }

        return mutations;
    }

    private async Task AdvanceClassSuitInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClassSuitCommandContext context,
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
            context.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Class Suit inventory revision did not advance exactly once.");
        }
    }

    private async Task InsertClassSuitLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        ClassSuitCommandContext context,
        long inventoryRevision,
        IReadOnlyList<InventoryMutation> mutations,
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
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            command.Parameters.Clear();
            command.Parameters.AddWithValue("inboxId", inboxId);
            command.Parameters.AddWithValue(
                "accountId",
                context.Subject.AccountId);
            command.Parameters.AddWithValue(
                "characterId",
                context.Subject.CharacterId);
            command.Parameters.AddWithValue(
                "inventoryRevision",
                inventoryRevision);
            command.Parameters.AddWithValue(
                "entryOrdinal",
                checked((short)index));
            command.Parameters.AddWithValue(
                "itemInstanceId",
                mutation.ItemInstanceId);
            command.Parameters.AddWithValue(
                "mutationKind",
                mutation.MutationKind);
            AddJsonParameter(command, "beforeState", mutation.BeforeState);
            AddJsonParameter(command, "afterState", mutation.AfterState);
            command.Parameters.AddWithValue(
                "reasonCode",
                ClassSuitPersistenceCodec.FamilyCode(context.Family));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Class Suit ledger append was not exact.");
            }
        }
    }

    private async Task InsertClassSuitOutboxAsync(
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
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            ClassSuitPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ClassSuitPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "eventType",
            ClassSuitPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            ClassSuitPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            ClassSuitPersistenceCodec.OrderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value = Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Class Suit outbox insert was not exact.");
        }
    }
}
