using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendEquipmentBagTransferRejectionRefreshAsync(
        int equipmentSlot,
        int bagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var equipmentRefresh =
            PacketBuilder.EquipmentItemSnapshot(_character, equipmentSlot);
        if (equipmentRefresh.Length == 0)
        {
            equipmentRefresh =
                PacketBuilder.EquipmentItemClearSnapshot(equipmentSlot);
        }

        await _session.SendAsync(
            equipmentRefresh,
            cancellationToken,
            "RejectedStorageEquipmentRefresh");
        await _session.SendAsync(
            PacketBuilder.KitBagSlotIndex(_character, bagSlot),
            cancellationToken,
            "RejectedStorageKitBagIndexRefresh");
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "RejectedStorageEquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "RejectedStoragePlayerDetailRefreshAck");
    }

    private async Task SendEquipRejectionRefreshAsync(
        int requestedEquipmentSlot,
        int resolvedEquipmentSlot,
        int bagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var equipmentSlot = ResolveEquipmentRejectionRefreshSlot(
            requestedEquipmentSlot,
            resolvedEquipmentSlot);
        if (EquipmentSlots.IsEquipmentSlot(equipmentSlot))
        {
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                bagSlot,
                cancellationToken);
            return;
        }

        await _session.SendAsync(
            PacketBuilder.KitBagSlotIndex(_character, bagSlot),
            cancellationToken,
            "RejectedEquipKitBagIndexRefresh");
    }

    internal static int ResolveEquipmentRejectionRefreshSlot(
        int requestedEquipmentSlot,
        int resolvedEquipmentSlot)
    {
        if (EquipmentSlots.IsEquipmentSlot(resolvedEquipmentSlot))
        {
            return resolvedEquipmentSlot;
        }

        return EquipmentSlots.IsEquipmentSlot(requestedEquipmentSlot)
            ? requestedEquipmentSlot
            : -1;
    }

    internal static EquipmentBagTransferAction
        ResolveEquipmentBagTransferAction(
            Godswar.Server.Application.Items.IItemTemplateCatalog templates,
            GameCharacter character,
            int equipmentSlot,
            int bagSlot)
    {
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot) ||
            bagSlot is < 0 or >= 96)
        {
            return EquipmentBagTransferAction.Reject;
        }

        var equippedItem = EquipmentSlots.GetItem(
            character.Equipment,
            character.Profession,
            equipmentSlot);
        var bagItem = KitBagSlots.GetItem(character.KitBag, bagSlot);
        if (!equippedItem.IsEmpty && bagItem.IsEmpty)
        {
            return EquipmentBagTransferAction.Unequip;
        }

        if (equippedItem.IsEmpty &&
            !bagItem.IsEmpty &&
            EquipmentSlots.ResolveSlotForItem(
                templates,
                bagItem.Id,
                equipmentSlot) == equipmentSlot)
        {
            return EquipmentBagTransferAction.Equip;
        }

        return EquipmentBagTransferAction.Reject;
    }
}
