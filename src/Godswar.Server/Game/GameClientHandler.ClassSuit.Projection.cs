using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task ReloadDurableClassSuitProjectionAsync(
        PlayerOwnershipFence ownership,
        ClassSuitExecutionReceipt receipt,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            _processRealmId,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The Class Suit owner changed during projection reload.");
        }

        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable Class Suit character could not be reloaded.");
        }
        ValidateClassSuitProjection(hydrated.Character, receipt);
        ApplyDurableEquipmentBagTransferProjection(
            _character,
            hydrated.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
    }

    private static void ValidateClassSuitProjection(
        GameCharacter persistedCharacter,
        ClassSuitExecutionReceipt receipt)
    {
        foreach (var mutation in receipt.Mutations)
        {
            var projected = mutation.Location switch
            {
                ClassSuitItemLocation.Equipment =>
                    EquipmentSlots.GetItem(
                        persistedCharacter.Equipment,
                        persistedCharacter.Profession,
                        mutation.KitBagSlot),
                ClassSuitItemLocation.KitBag =>
                    KitBagSlots.GetItem(
                        persistedCharacter.KitBag,
                        mutation.KitBagSlot),
                _ => throw new InvalidDataException(
                    "The Class Suit receipt has an invalid item location.")
            };
            if (!string.Equals(
                    projected.ToCompactString(),
                    mutation.AfterCompactItemState,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The committed Class Suit projection is stale.");
            }
        }
    }

    private async Task SendClassSuitAuthoritativeProjectionAsync(
        ClassSuitExecutionReceipt receipt,
        string reason,
        CancellationToken cancellationToken)
    {
        var equipmentSlots = ResolveClassSuitEquipmentRefreshSlots(
            receipt);
        if (equipmentSlots.Length == 0)
        {
            await SendKitBagRefreshAsync(cancellationToken);
            return;
        }

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "ClassSuitPlayerStatus");
        foreach (var slot in equipmentSlots)
        {
            var packet = PacketBuilder.EquipmentItemSnapshot(
                _character!,
                slot);
            if (packet.Length == 0)
            {
                packet = PacketBuilder.EquipmentItemClearSnapshot(slot);
            }
            await _session.SendAsync(
                packet,
                cancellationToken,
                "ClassSuitEquipmentRefresh");
        }
        await SendKitBagRefreshAsync(cancellationToken);
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(
                _character!,
                _itemContent?.FashionAppearances),
            cancellationToken,
            "ClassSuitVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                LocalPlayerObjectId,
                ResolveEquipmentEffectProjection(_character!)),
            cancellationToken,
            "ClassSuitEquipmentEffectVisibility");
        await BroadcastEquipmentRefreshAsync(
            $"class-suit-{reason}",
            cancellationToken);
    }

    internal static int[] ResolveClassSuitEquipmentRefreshSlots(
        ClassSuitExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var selectedEquipment = receipt.ReplayIntent.GearLocation ==
            ClassSuitItemLocation.Equipment
            ? new[] { receipt.ReplayIntent.GearKitBagSlot }
            : [];
        return receipt.Mutations
            .Where(static value =>
                value.Location == ClassSuitItemLocation.Equipment)
            .Select(static value => value.KitBagSlot)
            .Concat(selectedEquipment)
            .Distinct()
            .ToArray();
    }
}
