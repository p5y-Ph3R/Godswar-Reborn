using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<bool>
        ValidateAuthoritativeEquipmentEligibilityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            int propId,
            int equipmentSlot,
            LockedCharacter character,
            CancellationToken cancellationToken)
    {
        var equippedItems = new Dictionary<int, uint>();
        await using var command = CreateCommand(
            """
            SELECT slot_index, prop_id
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 0
            ORDER BY slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(0);
            var itemId = reader.GetInt32(1);
            if (!EquipmentSlots.IsEquipmentSlot(slot) ||
                itemId <= 0 ||
                !equippedItems.TryAdd(
                    slot,
                    checked((uint)itemId)))
            {
                throw new InvalidDataException(
                    "The authoritative equipment projection is invalid.");
            }
        }

        return EquipmentEligibility.ValidateEquip(
            _itemContent,
            checked((byte)character.Profession),
            character.Level,
            checked((uint)propId),
            equipmentSlot,
            slot => equippedItems.GetValueOrDefault(slot))
            .Allowed;
    }
}
