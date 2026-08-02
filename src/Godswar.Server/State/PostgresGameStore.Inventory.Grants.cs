using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<KitBagItemGrantResult> AddForgingMaterialAsync(
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        if (!ItemContent.DeveloperItems.TryResolveDeveloper(itemId, out var material))
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemId),
                "Item is not in the developer-item allowlist.");
        }

        if (quantity is < 1 or > KitBagItemGrantPlanner.MaximumQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT true
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null)
            {
                return new KitBagItemGrantResult(KitBagItemGrantStatus.CharacterNotFound, null);
            }
        }

        // Read and lock the authoritative rows directly. Do not round-trip the
        // character_item_loadout compatibility view here: that view deliberately
        // projects client-capped quality/grade values and rewriting it would
        // permanently lower unrelated high-ceiling bag items.
        var occupiedSlots = new HashSet<int>();
        var fillableStacks = new List<(long RowId, short Stack)>();
        await using (var command = new NpgsqlCommand("""
            SELECT id, slot_index, prop_id, bound, stack
            FROM character_items
            WHERE user_id = @characterId AND item_location = @kitBagLocation
            ORDER BY slot_index
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rowId = reader.GetInt64(0);
                var slot = reader.GetInt16(1);
                var propId = reader.GetInt32(2);
                var bound = reader.GetInt16(3);
                var stack = reader.GetInt16(4);
                if (slot is < 0 or >= KitBagItemGrantPlanner.SlotCount)
                {
                    continue;
                }

                occupiedSlots.Add(slot);
                if (propId == checked((int)itemId) &&
                    bound == material.GrantedBound &&
                    stack < material.StackCap)
                {
                    fillableStacks.Add((rowId, stack));
                }
            }
        }

        var emptySlots = Enumerable.Range(0, KitBagItemGrantPlanner.SlotCount)
            .Where(slot => !occupiedSlots.Contains(slot))
            .ToArray();
        var capacity = fillableStacks.Sum(stack =>
            Math.Max(0, material.StackCap - stack.Stack)) +
            ((long)emptySlots.Length * material.StackCap);
        if (capacity < quantity)
        {
            await transaction.CommitAsync(cancellationToken);
            return new KitBagItemGrantResult(
                KitBagItemGrantStatus.InsufficientCapacity,
                await GetCharacterByIdAsync(characterId, cancellationToken));
        }

        var remaining = quantity;
        await using (var updateStack = new NpgsqlCommand("""
            UPDATE character_items
            SET stack = @stack,
                updated_at = now()
            WHERE id = @rowId;
            """, connection, transaction))
        {
            foreach (var existing in fillableStacks)
            {
                if (remaining == 0)
                {
                    break;
                }

                var added = Math.Min(remaining, material.StackCap - existing.Stack);
                var updatedStack = checked((short)(existing.Stack + added));
                updateStack.Parameters.Clear();
                updateStack.Parameters.AddWithValue("stack", updatedStack);
                updateStack.Parameters.AddWithValue("rowId", existing.RowId);
                var updated = await updateStack.ExecuteNonQueryAsync(cancellationToken);
                if (updated != 1)
                {
                    throw new InvalidOperationException(
                        $"Kit-bag stack row {existing.RowId} changed while granting a developer material.");
                }

                remaining -= added;
            }
        }

        await using var insertItem = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code
            )
            VALUES (
                @characterId, @itemLocation, @slotIndex, @itemId,
                1, 1, @bound, @stack, 0, 0
            )
            ON CONFLICT (user_id, item_location, slot_index) DO NOTHING;
            """, connection, transaction);
        foreach (var slot in emptySlots)
        {
            if (remaining == 0)
            {
                break;
            }

            var stack = Math.Min(remaining, material.StackCap);
            insertItem.Parameters.Clear();
            insertItem.Parameters.AddWithValue("characterId", characterId);
            insertItem.Parameters.AddWithValue("itemLocation", ItemLocationKitBag);
            insertItem.Parameters.AddWithValue("slotIndex", (short)slot);
            insertItem.Parameters.AddWithValue("itemId", checked((int)itemId));
            insertItem.Parameters.AddWithValue("bound", material.GrantedBound);
            insertItem.Parameters.AddWithValue("stack", checked((short)stack));
            var inserted = await insertItem.ExecuteNonQueryAsync(cancellationToken);
            if (inserted != 1)
            {
                throw new InvalidOperationException(
                    $"Kit-bag slot {slot} changed while granting a developer material.");
            }

            remaining -= stack;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException("Validated developer-material capacity was not fully consumed.");
        }

        await transaction.CommitAsync(cancellationToken);

        return new KitBagItemGrantResult(
            KitBagItemGrantStatus.Added,
            await GetCharacterByIdAsync(characterId, cancellationToken));
    }

    public async Task<KitBagItemGrantResult> AddDeveloperMountAsync(
        int accountId,
        int characterId,
        uint itemId,
        CancellationToken cancellationToken = default)
    {
        if (!ItemContent.DeveloperMounts.TryResolveGrantable(itemId, out _))
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemId),
                "Item is not in the developer mount allowlist.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT true
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new KitBagItemGrantResult(
                    KitBagItemGrantStatus.CharacterNotFound,
                    null);
            }
        }

        int? emptySlot;
        await using (var command = new NpgsqlCommand("""
            SELECT candidate.slot_index
            FROM generate_series(0, @lastKitBagSlot) AS candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1
                FROM character_items existing
                WHERE existing.user_id = @characterId
                  AND existing.item_location = @kitBagLocation
                  AND existing.slot_index = candidate.slot_index
            )
            ORDER BY candidate.slot_index
            LIMIT 1;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(
                "lastKitBagSlot",
                KitBagItemGrantPlanner.SlotCount - 1);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            emptySlot = scalar is null || scalar is DBNull
                ? null
                : Convert.ToInt32(scalar);
        }

        if (!emptySlot.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new KitBagItemGrantResult(
                KitBagItemGrantStatus.InsufficientCapacity,
                await GetCharacterByIdAsync(characterId, cancellationToken));
        }

        await using (var command = new NpgsqlCommand("""
            WITH inserted AS (
                INSERT INTO character_items (
                    user_id, item_location, slot_index, prop_id,
                    item_quality, item_grade, bound, stack, item_exp, holy_suit_code
                )
                VALUES (
                    @characterId, @kitBagLocation, @slotIndex, @itemId,
                    1, 1, 1, 1, 0, 0
                )
                RETURNING *
            )
            INSERT INTO character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, item_quality, item_grade, item_exp, old_item
            )
            SELECT
                'developer-mount-grant',
                'insert',
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                item_exp,
                to_jsonb(inserted)
            FROM inserted;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
            command.Parameters.AddWithValue("slotIndex", checked((short)emptySlot.Value));
            command.Parameters.AddWithValue("itemId", checked((int)itemId));
            var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
            if (inserted != 1)
            {
                throw new InvalidOperationException(
                    "Validated developer mount was not inserted and audited exactly once.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new KitBagItemGrantResult(
            KitBagItemGrantStatus.Added,
            await GetCharacterByIdAsync(characterId, cancellationToken));
    }

}
