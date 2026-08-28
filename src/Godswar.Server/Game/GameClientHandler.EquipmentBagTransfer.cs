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
        if (action is EquipmentBagTransferAction.Equip or
            EquipmentBagTransferAction.Replace)
        {
            await HandleEquipItemAsync(
                bagSlot,
                requestedEquipmentSlot: equipmentSlot,
                itemIdHint: bagItem.Id,
                cancellationToken,
                sendStorageTransferAck: true,
                requireEmptyEquipmentSlot:
                    action == EquipmentBagTransferAction.Equip);
            return;
        }

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
