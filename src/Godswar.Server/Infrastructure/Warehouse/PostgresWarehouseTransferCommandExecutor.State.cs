using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseTransferCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        int realmId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT warehouse_capacity, warehouse_revision,
                   inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
              AND server_id = @realmId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        command.Parameters.AddWithValue("realmId", realmId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var result = new LockedCharacter(
            reader.GetInt16(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
        if (!WarehouseCapacityPolicy.IsValidCapacity(result.Capacity) ||
            result.WarehouseRevision < 0 ||
            result.InventoryRevision < 0)
        {
            throw new InvalidDataException(
                "The locked warehouse character state is invalid.");
        }
        return result;
    }

    private async Task<TransferPlan> BuildPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarehouseTransferCommand command,
        int characterId,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var endpoints = ResolveEndpoints(command);
        if (character.WarehouseRevision !=
                command.ExpectedWarehouseRevision ||
            character.InventoryRevision != command.ExpectedInventoryRevision)
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.ConcurrentConflict,
                endpoints,
                null,
                null);
        }
        if (WarehouseEndpointExceedsCapacity(
                endpoints.SourceLocation,
                endpoints.SourceSlot,
                character.Capacity) ||
            endpoints.DestinationSlot >= 0 &&
            WarehouseEndpointExceedsCapacity(
                endpoints.DestinationLocation,
                endpoints.DestinationSlot,
                character.Capacity))
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.CapacityExceeded,
                endpoints,
                null,
                null);
        }

        var source = await LockItemAsync(
            connection,
            transaction,
            characterId,
            endpoints.SourceLocation,
            endpoints.SourceSlot,
            cancellationToken);
        if (source is null)
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.EmptySource,
                endpoints,
                null,
                null);
        }
        if (source.LinkedSealedPet)
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.RestrictedItem,
                endpoints,
                source,
                null);
        }
        if (!string.Equals(
                source.Item.ToCompactString(),
                command.ExpectedSourceCompactItemState,
                StringComparison.Ordinal))
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.ConcurrentConflict,
                endpoints,
                source,
                null);
        }

        if (endpoints.DestinationSlot < 0)
        {
            return await PlanAutomaticAsync(
                connection,
                transaction,
                command,
                characterId,
                character,
                endpoints,
                source,
                cancellationToken);
        }

        var destination = await LockItemAsync(
            connection,
            transaction,
            characterId,
            endpoints.DestinationLocation,
            endpoints.DestinationSlot,
            cancellationToken);
        var destinationState = destination?.Item.ToCompactString() ?? "[]";
        if (!string.Equals(
                destinationState,
                command.ExpectedDestinationCompactItemState,
                StringComparison.Ordinal))
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.ConcurrentConflict,
                endpoints,
                source,
                destination);
        }

        if (destination?.LinkedSealedPet == true)
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.RestrictedItem,
                endpoints,
                source,
                destination);
        }
        return PlanExplicitMutation(command, endpoints, source, destination);
    }

    private TransferPlan PlanExplicitMutation(
        WarehouseTransferCommand command,
        TransferEndpoints endpoints,
        LockedItem source,
        LockedItem? destination)
    {
        if (destination is null)
        {
            var status = command.Operation switch
            {
                WarehouseTransferOperation.Deposit =>
                    WarehouseTransferResultStatus.Deposited,
                WarehouseTransferOperation.Withdraw =>
                    WarehouseTransferResultStatus.Withdrawn,
                WarehouseTransferOperation.InternalMove =>
                    WarehouseTransferResultStatus.InternalMoved,
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
            var mutation = CreateMutation(
                source,
                endpoints.DestinationLocation,
                endpoints.DestinationSlot,
                source.Item.Stack);
            return CreatePlan(
                command,
                status,
                endpoints,
                source,
                null,
                [],
                source.Item.Stack,
                source.Item.Stack,
                [mutation]);
        }

        var swapMutations = new[]
        {
            CreateMutation(
                source,
                endpoints.DestinationLocation,
                endpoints.DestinationSlot,
                source.Item.Stack),
            CreateMutation(
                destination,
                endpoints.SourceLocation,
                endpoints.SourceSlot,
                destination.Item.Stack)
        };
        return CreatePlan(
            command,
            WarehouseTransferResultStatus.Swapped,
            endpoints,
            source,
            destination,
            [],
            source.Item.Stack,
            source.Item.Stack,
            swapMutations);
    }

    private async Task<TransferPlan> PlanAutomaticAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarehouseTransferCommand command,
        int characterId,
        LockedCharacter character,
        TransferEndpoints endpoints,
        LockedItem source,
        CancellationToken cancellationToken)
    {
        int stackCap;
        try
        {
            stackCap = WarehousePinnedItemPolicy.ReadStackCap(
                _itemTemplates,
                checked((int)source.Item.Id));
        }
        catch (InvalidDataException)
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.RestrictedItem,
                endpoints,
                source,
                null);
        }
        if (source.Item.Stack > stackCap)
        {
            return Rejected(
                command,
                WarehouseTransferResultStatus.RestrictedItem,
                endpoints,
                source,
                null);
        }
        var slotLimit = DestinationLimit(
            endpoints.DestinationLocation,
            character);
        var items = await LockLocationItemsAsync(
            connection,
            transaction,
            characterId,
            endpoints.DestinationLocation,
            slotLimit,
            cancellationToken);
        var bySlot = items.ToDictionary(static item => (int)item.Slot);
        var targets = new List<LockedItem>();
        var targetMutations = new List<WarehouseItemMutation>();
        var remaining = (int)source.Item.Stack;
        var sourceAtCap = source.Item.Stack == stackCap;
        var firstTargetSlot = -1;
        for (var slot = 0; slot < slotLimit; slot++)
        {
            if (!bySlot.TryGetValue(slot, out var item))
            {
                var resolved = endpoints with { DestinationSlot = slot };
                var sourceMutation = CreateMutation(
                    source,
                    endpoints.DestinationLocation,
                    slot,
                    remaining);
                var mutations = new List<WarehouseItemMutation>(
                    1 + targetMutations.Count) { sourceMutation };
                mutations.AddRange(targetMutations);
                var status = targets.Count == 0
                    ? command.Operation == WarehouseTransferOperation.Deposit
                        ? WarehouseTransferResultStatus.Deposited
                        : WarehouseTransferResultStatus.Withdrawn
                    : WarehouseTransferResultStatus.Stacked;
                return CreatePlan(
                    command,
                    status,
                    resolved,
                    source,
                    null,
                    targets,
                    source.Item.Stack,
                    remaining,
                    mutations);
            }
            if (sourceAtCap || item.LinkedSealedPet ||
                !StackCompatible(source.Item, item.Item))
            {
                continue;
            }
            var available = stackCap - item.Item.Stack;
            if (available <= 0)
            {
                continue;
            }
            var pushed = Math.Min(remaining, available);
            remaining -= pushed;
            firstTargetSlot = firstTargetSlot < 0 ? slot : firstTargetSlot;
            targets.Add(item);
            targetMutations.Add(CreateMutation(
                item,
                endpoints.DestinationLocation,
                slot,
                item.Item.Stack + pushed));
            if (remaining == 0)
            {
                var resolved = endpoints with
                {
                    DestinationSlot = firstTargetSlot
                };
                var mutations = new List<WarehouseItemMutation>(
                    1 + targetMutations.Count)
                {
                    CreateMutation(source, null, null, null)
                };
                mutations.AddRange(targetMutations);
                return CreatePlan(
                    command,
                    WarehouseTransferResultStatus.Stacked,
                    resolved,
                    source,
                    null,
                    targets,
                    source.Item.Stack,
                    0,
                    mutations);
            }
        }
        if (targets.Count > 0)
        {
            var resolved = endpoints with { DestinationSlot = firstTargetSlot };
            var mutations = new List<WarehouseItemMutation>(
                1 + targetMutations.Count)
            {
                CreateMutation(
                    source,
                    endpoints.SourceLocation,
                    endpoints.SourceSlot,
                    remaining)
            };
            mutations.AddRange(targetMutations);
            return CreatePlan(
                command,
                WarehouseTransferResultStatus.Stacked,
                resolved,
                source,
                null,
                targets,
                source.Item.Stack - remaining,
                remaining,
                mutations);
        }
        var fullStatus = endpoints.DestinationLocation == 1
            ? WarehouseTransferResultStatus.BagFull
            : WarehouseTransferResultStatus.CapacityExceeded;
        return Rejected(command, fullStatus, endpoints, source, null);
    }

    private async Task<LockedItem?> LockItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short location,
        int slot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            ItemSelectSql(
                "item.item_location = @location AND item.slot_index = @slot"),
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("location", location);
        command.Parameters.AddWithValue("slot", checked((short)slot));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadLockedItem(reader)
            : null;
    }

    private async Task<IReadOnlyList<LockedItem>> LockLocationItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short location,
        int slotLimit,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            ItemSelectSql(
                "item.item_location = @location AND item.slot_index >= 0 " +
                "AND item.slot_index < @slotLimit",
                orderBy: true),
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("location", location);
        command.Parameters.AddWithValue("slotLimit", checked((short)slotLimit));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<LockedItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadLockedItem(reader));
        }
        return items;
    }

    private static string ItemSelectSql(
        string predicate,
        bool orderBy = false) =>
        $"""
        SELECT item.id, item.item_location, item.slot_index,
               {WarehouseItemStateCodec.SelectCompactColumns},
               to_jsonb(item)::text,
               COALESCE(link.pet_id, 0)
        FROM public.character_items item
        LEFT JOIN public.sealed_pet_items link
          ON link.item_instance_id = item.id
        WHERE item.user_id = @characterId
          AND {predicate}
        {(orderBy ? "ORDER BY item.slot_index, item.id" : string.Empty)}
        FOR UPDATE OF item;
        """;

    private static LockedItem ReadLockedItem(NpgsqlDataReader reader)
    {
        var linkedPet = reader.GetInt64(42);
        var item = WarehouseItemStateCodec.ReadCompactItem(reader, 3) with
        {
            LinkedSealedPetId = linkedPet
        };
        return new LockedItem(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            item,
            reader.GetString(41),
            linkedPet > 0);
    }

}
