using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<int> ResolveRightClickRingSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int defaultEquipmentSlot,
        CancellationToken cancellationToken)
    {
        foreach (var slot in new[]
                 {
                     EquipmentSlots.Ring1,
                     EquipmentSlots.Ring2
                 })
        {
            if (!await EquipmentSlotOccupiedAsync(
                    connection,
                    transaction,
                    characterId,
                    slot,
                    cancellationToken))
            {
                return slot;
            }
        }

        return defaultEquipmentSlot is
            EquipmentSlots.Ring1 or EquipmentSlots.Ring2
                ? defaultEquipmentSlot
                : EquipmentSlots.Ring1;
    }

    private async Task<bool> EquipmentSlotOccupiedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int equipmentSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 0
              AND slot_index = @equipmentSlot
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "equipmentSlot",
            (short)equipmentSlot);
        return await command.ExecuteScalarAsync(cancellationToken)
            is not null;
    }

    private async Task<LockedEquipmentItem?> LockEquipmentItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int equipmentSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 0
              AND slot_index = @equipmentSlot
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "equipmentSlot",
            (short)equipmentSlot);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedEquipmentItem(
                reader.GetInt64(0),
                reader.GetString(1))
            : null;
    }

    private async Task<int> AllocateTemporaryItemSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT candidate.slot_index
            FROM generate_series(-32768, -1)
                AS candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1
                FROM public.character_items item
                WHERE item.user_id = @characterId
                  AND item.item_location = 2
                  AND item.slot_index = candidate.slot_index
            )
            ORDER BY candidate.slot_index
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? throw new InvalidDataException(
                "No temporary equipment-swap slot is available.")
            : Convert.ToInt32(value);
    }

    private async Task<string> MoveItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long itemId,
        int characterId,
        short itemLocation,
        int slot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET item_location = @itemLocation,
                slot_index = @slot,
                updated_at = transaction_timestamp()
            WHERE id = @itemId
              AND user_id = @characterId
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        command.Parameters.AddWithValue("slot", checked((short)slot));
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken)
            as string ?? throw new InvalidDataException(
                "The equipment move did not update exactly one item.");
    }

    private async Task<int> CountPetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.character_pets
            WHERE user_id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed record LockedEquipmentItem(
        long ItemId,
        string BeforeState);

    private async Task<long> AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var nextRevision = checked(expectedRevision + 1);
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @nextRevision
            WHERE id = @characterId
              AND inventory_revision = @expectedRevision
            RETURNING inventory_revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedRevision",
            expectedRevision);
        command.Parameters.AddWithValue("nextRevision", nextRevision);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == nextRevision
            ? revision
            : throw new InvalidDataException(
                "The pet bag activation inventory revision was not exact.");
    }
}
