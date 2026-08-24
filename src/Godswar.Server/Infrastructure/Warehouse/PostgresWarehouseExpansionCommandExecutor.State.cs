using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Npgsql;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseExpansionCommandExecutor
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
                "The locked warehouse expansion state is invalid.");
        }
        return result;
    }

    private async Task<ExpansionPlan> BuildPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarehouseExpansionCommand command,
        int characterId,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (command.ExpectedCapacity > _policy.MaximumCapacity ||
            command.TargetCapacity !=
                _policy.NextLevelForCapacity(command.ExpectedCapacity)
                    .Capacity)
        {
            throw new InvalidDataException(
                "The warehouse expansion command is outside pinned policy.");
        }

        var target = _policy.ForCapacity(command.TargetCapacity);
        if (character.Capacity != command.ExpectedCapacity)
        {
            return RejectedPlan(
                WarehouseExpansionResultStatus.CapacityConflict,
                character,
                target.KeyItemId,
                target.KeyCost);
        }
        if (character.Capacity == _policy.MaximumCapacity)
        {
            return RejectedPlan(
                WarehouseExpansionResultStatus.AlreadyMaximum,
                character,
                target.KeyItemId,
                requiredKeys: 0);
        }

        var keys = await LockKeyItemsAsync(
            connection,
            transaction,
            characterId,
            target.KeyItemId,
            cancellationToken);
        var total = keys.Aggregate(
            0,
            static (sum, item) => checked(sum + item.BeforeStack));
        if (total < target.KeyCost)
        {
            return RejectedPlan(
                WarehouseExpansionResultStatus.InsufficientKeys,
                character,
                target.KeyItemId,
                target.KeyCost);
        }

        var remaining = target.KeyCost;
        var consumed = new List<LockedKeyItem>();
        var mutations = new List<WarehouseItemMutation>();
        foreach (var key in keys)
        {
            if (remaining == 0)
            {
                break;
            }
            var count = Math.Min(key.BeforeStack, remaining);
            var after = key.BeforeStack - count;
            var planned = key with { AfterStack = after };
            consumed.Add(planned);
            mutations.Add(new WarehouseItemMutation(
                key.ItemInstanceId,
                target.KeyItemId,
                WarehouseInventoryLocation.KitBag,
                key.Slot,
                key.BeforeStack,
                after == 0 ? null : WarehouseInventoryLocation.KitBag,
                after == 0 ? null : key.Slot,
                after == 0 ? null : after));
            remaining -= count;
        }
        if (remaining != 0)
        {
            throw new InvalidDataException(
                "Locked warehouse keys did not satisfy the planned cost.");
        }
        return new ExpansionPlan(
            WarehouseExpansionResultStatus.Expanded,
            character.Capacity,
            command.TargetCapacity,
            target.KeyItemId,
            target.KeyCost,
            target.KeyCost,
            checked(character.WarehouseRevision + 1),
            checked(character.InventoryRevision + 1),
            consumed,
            mutations);
    }

    private async Task<IReadOnlyList<LockedKeyItem>> LockKeyItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT item.id, item.slot_index, item.stack,
                   to_jsonb(item)::text
            FROM public.character_items item
            WHERE item.user_id = @characterId
              AND item.item_location = 1
              AND item.prop_id = @itemId
            ORDER BY item.slot_index, item.id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemId", itemId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<LockedKeyItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var stack = reader.GetInt16(2);
            if (stack <= 0)
            {
                throw new InvalidDataException(
                    "A locked Storage Box Key stack is invalid.");
            }
            items.Add(new LockedKeyItem(
                reader.GetInt64(0),
                reader.GetInt16(1),
                stack,
                reader.GetString(3),
                stack));
        }
        return items;
    }

    private static ExpansionPlan RejectedPlan(
        WarehouseExpansionResultStatus status,
        LockedCharacter character,
        int keyItemId,
        int requiredKeys) =>
        new(
            status,
            character.Capacity,
            character.Capacity,
            keyItemId,
            requiredKeys,
            0,
            character.WarehouseRevision,
            character.InventoryRevision,
            [],
            []);
}
