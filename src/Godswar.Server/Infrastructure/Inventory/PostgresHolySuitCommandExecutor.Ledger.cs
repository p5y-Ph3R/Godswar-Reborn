using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
    private HolySuitExecutionReceipt CreateReceipt(
        HolySuitCommandContext context,
        LockedCharacter character,
        DailyUsage daily,
        bool battlePass,
        HolySuitPlan plan,
        IReadOnlyList<AppliedMutation> mutations,
        long progressionRevision,
        long inventoryRevision,
        long auditId,
        Guid eventId) =>
        new(
            context.Subject.CharacterId,
            context.Command.Operation,
            context.Command.NpcId,
            context.Command.DialogIndex,
            plan.Status,
            HolySuitNativeResults.GetResultSubId(
                context.Command.Operation,
                plan.Status),
            context.Command.ExperienceToStore,
            context.Command.PrismsToCreate,
            character.Experience,
            plan.CharacterExperienceAfter,
            daily.StoredExperience,
            plan.DailyStoredExperienceAfter,
            battlePass,
            plan.PrismsCreated,
            plan.PrismsConsumed,
            mutations.Select(static value =>
                new HolySuitReceiptMutation(
                    value.Role,
                    value.Slot,
                    value.ItemId,
                    value.ItemInstanceId,
                    value.BeforeCompactState,
                    value.AfterCompactState)).ToArray(),
            progressionRevision,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        HolySuitCommandContext context,
        long inventoryRevision,
        IReadOnlyList<AppliedMutation> mutations,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id, account_id, character_id,
                inventory_revision, entry_ordinal, item_instance_id,
                mutation_kind, state_contract_version, before_state,
                after_state, reason_code
            )
            VALUES (
                @inboxId, @accountId, @characterId,
                @inventoryRevision, @entryOrdinal, @itemInstanceId,
                @mutationKind, 1, @beforeState,
                @afterState, @reasonCode
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
                HolySuitPersistenceCodec.CommandFamilyCode(context.Family));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Holy Suit inventory ledger append was not exact.");
            }
        }
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandFamily family,
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
            HolySuitPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            HolySuitPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "eventType",
            HolySuitPersistenceCodec.EventType(family));
        command.Parameters.AddWithValue(
            "contractVersion",
            HolySuitPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            HolySuitPersistenceCodec.OrderingPolicy);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Holy Suit outbox insert was not exact.");
        }
    }

    private static void AddJsonParameter(
        NpgsqlCommand command,
        string name,
        string? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value =
            value is null ? DBNull.Value : value;
    }
}
