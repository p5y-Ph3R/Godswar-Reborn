using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseTransferCommandExecutor
{
    private async Task ApplyPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        var source = plan.Source ??
            throw new InvalidDataException(
                "A successful warehouse transfer has no source.");
        await InsertCompatibilityAuditAsync(
            connection,
            transaction,
            characterId,
            source,
            cancellationToken);
        if (plan.Destination is { } destination)
        {
            await InsertCompatibilityAuditAsync(
                connection,
                transaction,
                characterId,
                destination,
                cancellationToken);
        }
        foreach (var stackDestination in plan.StackDestinations)
        {
            await InsertCompatibilityAuditAsync(
                connection,
                transaction,
                characterId,
                stackDestination,
                cancellationToken);
        }

        switch (plan.Status)
        {
            case WarehouseTransferResultStatus.Deposited:
            case WarehouseTransferResultStatus.Withdrawn:
            case WarehouseTransferResultStatus.InternalMoved:
                await UpdatePositionAsync(
                    connection,
                    transaction,
                    characterId,
                    source.ItemInstanceId,
                    plan.SourceLocation,
                    plan.SourceSlot,
                    plan.DestinationLocation,
                    plan.DestinationSlot,
                    cancellationToken);
                return;
            case WarehouseTransferResultStatus.Stacked:
                await ApplyStackAsync(
                    connection,
                    transaction,
                    characterId,
                    plan,
                    cancellationToken);
                return;
            case WarehouseTransferResultStatus.Swapped:
                await ApplySwapAsync(
                    connection,
                    transaction,
                    characterId,
                    plan,
                    cancellationToken);
                return;
            default:
                throw new InvalidDataException(
                    "A rejected warehouse plan cannot mutate items.");
        }
    }

    private async Task ApplyStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        var source = plan.Source!;
        foreach (var destination in plan.StackDestinations)
        {
            var mutation = plan.Mutations.Single(value =>
                value.ItemInstanceId == destination.ItemInstanceId);
            await UpdateStackAsync(
                connection,
                transaction,
                characterId,
                destination.ItemInstanceId,
                destination.Location,
                destination.Slot,
                destination.Item.Stack,
                mutation.AfterStack ?? throw new InvalidDataException(
                    "A stack destination has no final count."),
                cancellationToken);
        }
        var sourceMutation = plan.Mutations.Single(value =>
            value.ItemInstanceId == source.ItemInstanceId);
        if (sourceMutation.AfterLocation is null)
        {
            await DeleteItemAsync(
                connection,
                transaction,
                characterId,
                source,
                cancellationToken);
        }
        else if (sourceMutation.AfterLocation !=
                    (WarehouseInventoryLocation)source.Location ||
                 sourceMutation.AfterSlot != source.Slot)
        {
            await UpdatePositionAndStackAsync(
                connection,
                transaction,
                characterId,
                source,
                checked((short)sourceMutation.AfterLocation.Value),
                sourceMutation.AfterSlot!.Value,
                sourceMutation.AfterStack!.Value,
                cancellationToken);
        }
        else
        {
            await UpdateStackAsync(
                connection,
                transaction,
                characterId,
                source.ItemInstanceId,
                source.Location,
                source.Slot,
                source.Item.Stack,
                plan.SourceAfterStack,
                cancellationToken);
        }
    }

    private async Task UpdatePositionAndStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedItem source,
        short destinationLocation,
        int destinationSlot,
        int destinationStack,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET item_location = @destinationLocation,
                slot_index = @destinationSlot,
                stack = @destinationStack,
                updated_at = transaction_timestamp()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = @sourceLocation
              AND slot_index = @sourceSlot
              AND stack = @sourceStack;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "destinationLocation",
            destinationLocation);
        command.Parameters.AddWithValue(
            "destinationSlot",
            checked((short)destinationSlot));
        command.Parameters.AddWithValue(
            "destinationStack",
            checked((short)destinationStack));
        command.Parameters.AddWithValue(
            "itemInstanceId",
            source.ItemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("sourceLocation", source.Location);
        command.Parameters.AddWithValue("sourceSlot", source.Slot);
        command.Parameters.AddWithValue("sourceStack", source.Item.Stack);
        await RequireOneAsync(
            command,
            "A partially stacked source did not move exactly once.",
            cancellationToken);
    }

    private async Task ApplySwapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        var source = plan.Source!;
        var destination = plan.Destination ??
            throw new InvalidDataException("A swap plan has no destination.");
        var temporarySlot = await FindPrivateTemporarySlotAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        await UpdatePositionAsync(
            connection,
            transaction,
            characterId,
            source.ItemInstanceId,
            plan.SourceLocation,
            plan.SourceSlot,
            2,
            temporarySlot,
            cancellationToken);
        await UpdatePositionAsync(
            connection,
            transaction,
            characterId,
            destination.ItemInstanceId,
            plan.DestinationLocation,
            plan.DestinationSlot,
            plan.SourceLocation,
            plan.SourceSlot,
            cancellationToken);
        await UpdatePositionAsync(
            connection,
            transaction,
            characterId,
            source.ItemInstanceId,
            2,
            temporarySlot,
            plan.DestinationLocation,
            plan.DestinationSlot,
            cancellationToken);
    }

    private async Task UpdatePositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemInstanceId,
        short sourceLocation,
        int sourceSlot,
        short destinationLocation,
        int destinationSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET item_location = @destinationLocation,
                slot_index = @destinationSlot,
                updated_at = transaction_timestamp()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = @sourceLocation
              AND slot_index = @sourceSlot;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "destinationLocation",
            destinationLocation);
        command.Parameters.AddWithValue(
            "destinationSlot",
            checked((short)destinationSlot));
        command.Parameters.AddWithValue("itemInstanceId", itemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("sourceLocation", sourceLocation);
        command.Parameters.AddWithValue(
            "sourceSlot",
            checked((short)sourceSlot));
        await RequireOneAsync(
            command,
            "A warehouse item did not move exactly once.",
            cancellationToken);
    }

    private async Task UpdateStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemInstanceId,
        short location,
        short slot,
        short beforeStack,
        int afterStack,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET stack = @afterStack,
                updated_at = transaction_timestamp()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = @location
              AND slot_index = @slot
              AND stack = @beforeStack;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "afterStack",
            checked((short)afterStack));
        command.Parameters.AddWithValue("itemInstanceId", itemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("location", location);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("beforeStack", beforeStack);
        await RequireOneAsync(
            command,
            "A warehouse stack did not update exactly once.",
            cancellationToken);
    }

    private async Task DeleteItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedItem item,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.character_items
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = @location
              AND slot_index = @slot
              AND stack = @stack
              AND NOT EXISTS (
                  SELECT 1
                  FROM public.sealed_pet_items link
                  WHERE link.item_instance_id = character_items.id
              );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemInstanceId", item.ItemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("location", item.Location);
        command.Parameters.AddWithValue("slot", item.Slot);
        command.Parameters.AddWithValue("stack", item.Item.Stack);
        await RequireOneAsync(
            command,
            "A consumed warehouse stack did not delete exactly once.",
            cancellationToken);
    }

    private async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        long expected,
        long next,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @next
            WHERE account_id = @accountId
              AND id = @characterId
              AND inventory_revision = @expected;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("next", next);
        command.Parameters.AddWithValue("expected", expected);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        await RequireOneAsync(
            command,
            "The warehouse inventory revision did not advance once.",
            cancellationToken);
    }

    private async Task InsertPlanLedgersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        long inboxId,
        long revision,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        var items = new[] { plan.Source, plan.Destination }
            .Where(static item => item is not null)
            .Select(static item => item!)
            .Concat(plan.StackDestinations)
            .DistinctBy(static item => item.ItemInstanceId)
            .ToArray();
        for (short ordinal = 0; ordinal < items.Length; ordinal++)
        {
            var item = items[ordinal];
            var mutation = plan.Mutations.Single(value =>
                value.ItemInstanceId == item.ItemInstanceId);
            var after = await ReadFullItemStateAsync(
                connection,
                transaction,
                subject.CharacterId,
                item.ItemInstanceId,
                cancellationToken);
            var kind = after is null
                ? "delete"
                : mutation.BeforeLocation != mutation.AfterLocation ||
                  mutation.BeforeSlot != mutation.AfterSlot
                    ? "move"
                    : "update";
            await InsertInventoryLedgerAsync(
                connection,
                transaction,
                inboxId,
                subject,
                revision,
                ordinal,
                item,
                after,
                kind,
                cancellationToken);
        }
    }

    private async Task<string?> ReadFullItemStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemInstanceId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT to_jsonb(item)::text
            FROM public.character_items item
            WHERE item.id = @itemInstanceId
              AND item.user_id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemInstanceId", itemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandSubject subject,
        long revision,
        short ordinal,
        LockedItem item,
        string? after,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id, account_id, character_id,
                inventory_revision, entry_ordinal, item_instance_id,
                mutation_kind, state_contract_version, before_state,
                after_state, reason_code)
            VALUES (
                @inboxId, @accountId, @characterId, @revision, @ordinal,
                @itemInstanceId, @kind, 1, @beforeState, @afterState,
                @reasonCode);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("ordinal", ordinal);
        command.Parameters.AddWithValue("itemInstanceId", item.ItemInstanceId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.Add("beforeState", NpgsqlDbType.Jsonb).Value =
            item.BeforeState;
        command.Parameters.Add("afterState", NpgsqlDbType.Jsonb).Value =
            after is null ? DBNull.Value : after;
        command.Parameters.AddWithValue(
            "reasonCode",
            WarehouseTransferPersistenceCodec.LedgerReasonCode);
        await RequireOneAsync(
            command,
            "The warehouse inventory ledger append was not exact.",
            cancellationToken);
    }

    private static async Task RequireOneAsync(
        NpgsqlCommand command,
        string message,
        CancellationToken cancellationToken)
    {
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(message);
        }
    }
}
