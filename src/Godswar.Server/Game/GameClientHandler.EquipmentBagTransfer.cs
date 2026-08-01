using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleEquipmentBagTransferAsync(
        int equipmentSlot,
        int bagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var action = ResolveEquipmentBagTransferAction(
            RequireItemContent().Templates,
            _character,
            equipmentSlot,
            bagSlot);
        if (action == EquipmentBagTransferAction.Unequip)
        {
            await HandleUnequipItemAsync(
                equipmentSlot,
                bagSlot,
                cancellationToken);
            return;
        }

        var equippedItem = EquipmentSlots.GetItem(
            _character.Equipment,
            _character.Profession,
            equipmentSlot);
        var bagItem = KitBagSlots.GetItem(
            _character.KitBag,
            bagSlot);
        if (action == EquipmentBagTransferAction.Equip)
        {
            await HandleEquipItemAsync(
                bagSlot,
                requestedEquipmentSlot: equipmentSlot,
                itemIdHint: bagItem.Id,
                cancellationToken,
                sendStorageTransferAck: true);
            return;
        }

        // Opcode 10052 has no direction bit. The native client treats a pair
        // of occupied locations as a swap. Reject it so dropping equipped
        // gear onto an occupied bag slot cannot unequip it.
        Console.WriteLine(
            $"[equip-re] StorageItem transfer ignored: " +
            $"equipmentSlot={equipmentSlot} " +
            $"equipmentItem={equippedItem.Id} " +
            $"bagSlot={bagSlot} bagItem={bagItem.Id}");
        await SendEquipmentBagTransferRejectionRefreshAsync(
            equipmentSlot,
            bagSlot,
            cancellationToken);
    }
}
