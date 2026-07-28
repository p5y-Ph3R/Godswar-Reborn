using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleStorageItemAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine("[equip-re] StorageItem ignored: no active character");
            return;
        }

        if (TryReadStorageItemEquipmentBagTransfer(packet.Payload, out var equipmentSlot, out var bagSlot))
        {
            await HandleEquipmentBagTransferAsync(equipmentSlot, bagSlot, cancellationToken);
            return;
        }

        if (TryReadStorageItemDelete(packet.Payload, out var deletedSlot))
        {
            await HandleDeleteKitBagItemAsync(deletedSlot, cancellationToken);
            return;
        }

        if (TryReadStorageItemKitBagMove(packet.Payload, out var moveSourceSlot, out var moveDestinationSlot))
        {
            await HandleMoveKitBagItemAsync(moveSourceSlot, moveDestinationSlot, cancellationToken);
            return;
        }

        Console.WriteLine("[equip-re] StorageItem ignored: payload does not match known equip/unequip shapes");
    }

    private async Task HandleEquipmentBagTransferAsync(
        int equipmentSlot,
        int bagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var action = ResolveEquipmentBagTransferAction(_character, equipmentSlot, bagSlot);
        if (action == EquipmentBagTransferAction.Unequip)
        {
            await HandleUnequipItemAsync(equipmentSlot, bagSlot, cancellationToken);
            return;
        }

        var equippedItem = EquipmentSlots.GetItem(
            _character.Equipment,
            _character.Profession,
            equipmentSlot);
        var bagItem = KitBagSlots.GetItem(_character.KitBag, bagSlot);
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

        // Opcode 10052 has no direction bit. The native client treats a pair of
        // occupied locations as a swap, but this server deliberately rejects it so
        // dropping equipped gear onto an occupied bag slot cannot unequip it.
        Console.WriteLine(
            $"[equip-re] StorageItem transfer ignored: equipmentSlot={equipmentSlot} equipmentItem={equippedItem.Id} bagSlot={bagSlot} bagItem={bagItem.Id}");
        await SendEquipmentBagTransferRejectionRefreshAsync(
            equipmentSlot,
            bagSlot,
            cancellationToken);
    }

    private async Task SendEquipmentBagTransferRejectionRefreshAsync(
        int equipmentSlot,
        int bagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var equipmentRefresh = PacketBuilder.EquipmentItemSnapshot(_character, equipmentSlot);
        if (equipmentRefresh.Length == 0)
        {
            equipmentRefresh = PacketBuilder.EquipmentItemClearSnapshot(equipmentSlot);
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

    internal static EquipmentBagTransferAction ResolveEquipmentBagTransferAction(
        GameCharacter character,
        int equipmentSlot,
        int bagSlot)
    {
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot) || bagSlot is < 0 or >= 96)
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

        if (equippedItem.IsEmpty
            && !bagItem.IsEmpty
            && EquipmentSlots.ResolveSlotForItem(bagItem.Id, equipmentSlot) == equipmentSlot)
        {
            return EquipmentBagTransferAction.Equip;
        }

        return EquipmentBagTransferAction.Reject;
    }

    private async Task HandleBreakItemAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine("[equip-re] BreakItem ignored: no active character");
            return;
        }

        if (!TryReadBreakItemEquip(packet.Payload, out var sourceSlot))
        {
            Console.WriteLine("[equip-re] BreakItem ignored: payload does not contain a valid bag page/index");
            return;
        }

        var itemId = KitBagSlots.GetItemId(_character.KitBag, sourceSlot);
        if (PetSpeciesCatalog.TryGetByEggItemId(itemId, out _))
        {
            await HandlePetEggHatchAsync(
                sourceSlot,
                cancellationToken);
            return;
        }

        if (!EquipmentSlots.TryGetAuthoritativeSlot(itemId, out _))
        {
            Console.WriteLine(
                $"[equip-re] BreakItem ignored: sourceSlot={sourceSlot} item={itemId} is not genuine equipment");
            return;
        }

        await HandleEquipItemAsync(sourceSlot, requestedEquipmentSlot: -1, itemIdHint: 0, cancellationToken);
    }

    private async Task HandleUseOrEquipAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (!TryReadTalentUpgrade(packet.Payload, out var talentId, out var clientRank, out var clientTalentPoints))
        {
            Console.WriteLine("[talent] UseOrEquip ignored: payload does not match captured talent-upgrade shape");
            return;
        }

        if (_account is null || _character is null)
        {
            Console.WriteLine("[talent] upgrade ignored: no active character");
            return;
        }

        var result = await _store.UpgradeTalentAsync(
            _account.Id,
            _character.Id,
            talentId,
            clientRank,
            clientTalentPoints,
            cancellationToken);

        if (result is null)
        {
            Console.WriteLine(
                $"[talent] upgrade failed character={_character.Name} talent={talentId} clientRank={clientRank} clientPoints={clientTalentPoints}");
            return;
        }

        _character = result.Character;
        await RefreshActiveCharacterStatsAsync("talent-upgrade", cancellationToken);
        _registry.UpdateCharacter(_session, _character);
        Console.WriteLine(
            $"[talent] upgraded character={_character.Name} talent={result.TalentId} rank={result.NewRank} cost={result.Cost} remaining={result.RemainingTalentPoints} value={result.DisplayValue}");

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PlayerStatusUpdate");
        await _session.SendAsync(
            PacketBuilder.TalentUpgradeAck(result),
            cancellationToken,
            "TalentUpgradeAck");
    }

    private async Task HandleBagItemActionAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogInventoryPacket(packet);

        if (_account is null || _character is null)
        {
            Console.WriteLine("[equip-re] BagItemAction ignored: no active character");
            return;
        }

        if (!TryReadBagItemAction(packet.Payload, out var sourceSlot, out var itemId))
        {
            Console.WriteLine("[equip-re] BagItemAction ignored: payload does not match captured bag-to-equipment shape");
            return;
        }

        if (TryConsumeUnequipFollowup(sourceSlot, itemId))
        {
            Console.WriteLine(
                $"[equip-re] BagItemAction unequip follow-up acknowledged character={_character.Name} sourceSlot={sourceSlot} item={itemId}");
            await _session.SendAsync(
                PacketBuilder.BagItemActionAck(packet.Buffer),
                cancellationToken,
                "BagItemActionAck");
            return;
        }

        Console.WriteLine(
            MatchesCurrentKitBagItem(_character, sourceSlot, itemId)
                ? $"[equip-re] BagItemAction acknowledged character={_character.Name} sourceSlot={sourceSlot} item={itemId}"
                : $"[equip-re] BagItemAction acknowledged without matching authoritative item sourceSlot={sourceSlot} item={itemId}");

        await _session.SendAsync(
            PacketBuilder.BagItemActionAck(packet.Buffer),
            cancellationToken,
            "BagItemActionInspectAck");
    }

    private void HandleItemInfoRequest(GamePacket packet)
    {
        LogInventoryPacket(packet);

        Console.WriteLine(
            TryReadItemInfoRequest(packet.Payload, out var sourceSlot, out var itemId)
            && MatchesCurrentKitBagItem(_character, sourceSlot, itemId)
                ? $"[equip-re] ItemInfoRequest sourceSlot={sourceSlot} item={itemId}"
                : "[equip-re] ItemInfoRequest ignored: payload does not match the authoritative kitbag item");
    }

    private async Task HandleUnequipItemAsync(int equipmentSlot, int destinationSlot, CancellationToken cancellationToken)
    {
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot) || destinationSlot is < 0 or >= 96)
        {
            Console.WriteLine($"[equip-re] StorageItem unequip ignored: unsupported slot={equipmentSlot} destination={destinationSlot}");
            return;
        }

        if (_account is null || _character is null)
        {
            return;
        }

        var previousEquipmentEntry = EquipmentSlots.GetEntry(
            _character.Equipment,
            _character.Profession,
            equipmentSlot);
        var previousItemId = CompactItemEntry.Parse(previousEquipmentEntry).Id;
        if (previousItemId == 0)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem unequip ignored: empty equipment slot={equipmentSlot} destination={destinationSlot}");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            return;
        }

        if (equipmentSlot == EquipmentSlots.Mount &&
            (IsSkillCastPending(MountCatalog.RideSkillId) ||
             _registry.IsRuntimeStatusActive(
                 _session,
                 MountCatalog.RuntimeStatusKind,
                 DateTimeOffset.UtcNow)))
        {
            Console.WriteLine(
                $"[mount] rejected mount removal while Ride is active or pending character={_character.Name} item={previousItemId}");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            return;
        }

        var unequipEligibility = EquipmentEligibility.ValidateUnequip(
            _character.Profession,
            _character.Equipment,
            equipmentSlot);
        if (!unequipEligibility.Allowed)
        {
            Console.WriteLine(
                $"[mount] rejected unequip character={_character.Name} slot={equipmentSlot}: {unequipEligibility.Reason}");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            return;
        }

        var previousKitBag = _character.KitBag;
        var updatedCharacter = await _store.MoveEquipmentToKitBagAsync(
            _account.Id,
            _character.Id,
            equipmentSlot,
            kitBagSlot: destinationSlot,
            cancellationToken: cancellationToken);

        if (updatedCharacter is null)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem unequip failed: character={_character.Name} id={_character.Id} slot={equipmentSlot}");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            return;
        }

        if (previousItemId != 0
            && EquipmentSlots.GetItemId(updatedCharacter.Equipment, updatedCharacter.Profession, equipmentSlot) == previousItemId)
        {
            _character = updatedCharacter;
            Console.WriteLine(
                $"[equip-re] StorageItem unequip did not move item: character={_character.Name} slot={equipmentSlot} item={previousItemId} destination={destinationSlot}");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            return;
        }

        var actualDestinationSlot = ResolveMovedKitBagDestination(
            previousKitBag,
            updatedCharacter.KitBag,
            previousEquipmentEntry);
        if (actualDestinationSlot != destinationSlot)
        {
            _character = updatedCharacter;
            await RefreshActiveCharacterStatsAsync("unequip-destination-mismatch", cancellationToken);
            _registry.UpdateCharacter(_session, _character);
            Console.WriteLine(
                $"[equip-re] StorageItem unequip destination mismatch: character={_character.Name} slot={equipmentSlot} item={previousItemId} actualDestination={actualDestinationSlot} requestedDestination={destinationSlot}");
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "PlayerStatusUpdate");
            await SendEquipmentBagTransferRejectionRefreshAsync(
                equipmentSlot,
                destinationSlot,
                cancellationToken);
            if (actualDestinationSlot is >= 0 and < 96)
            {
                await _session.SendAsync(
                    PacketBuilder.KitBagSlotIndex(_character, actualDestinationSlot),
                    cancellationToken,
                    "RejectedStorageActualKitBagIndexRefresh");
            }
            return;
        }

        _character = updatedCharacter;
        await RefreshActiveCharacterStatsAsync("unequip", cancellationToken);
        _registry.UpdateCharacter(_session, _character);
        _pendingUnequipFollowup = previousItemId == 0
            ? null
            : new PendingUnequipFollowup(actualDestinationSlot, previousItemId, DateTime.UtcNow);

        var clientEquipmentSlot = PacketBuilder.ToClientEquipmentSlot(equipmentSlot);
        Console.WriteLine(
            $"[equip-re] unequipped character={_character.Name} slot={equipmentSlot} clientSlot={clientEquipmentSlot} previousItem={previousItemId} destination={actualDestinationSlot} requestedDestination={destinationSlot} equipment={PacketBuilder.EnterEquipmentSummary(_character)}");

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PlayerStatusUpdate");
        await _session.SendAsync(
            PacketBuilder.StorageItemEquipmentBagTransfer(clientEquipmentSlot, actualDestinationSlot),
            cancellationToken,
            "StorageItemUnequipAck");

        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "EquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "PlayerDetailRefreshAck");
        await BroadcastEquipmentRefreshAsync("unequip", cancellationToken);
    }

    internal static int ResolveMovedKitBagDestination(
        string previousKitBag,
        string updatedKitBag,
        string movedEquipmentEntry)
    {
        var movedItem = CompactItemEntry.Parse(movedEquipmentEntry);
        if (movedItem.IsEmpty)
        {
            return -1;
        }

        for (var slot = 0; slot < 96; slot++)
        {
            var before = KitBagSlots.GetItem(previousKitBag, slot);
            var after = KitBagSlots.GetItem(updatedKitBag, slot);
            if (before.IsEmpty && after == movedItem)
            {
                return slot;
            }
        }

        return -1;
    }

    private bool TryConsumeUnequipFollowup(int sourceSlot, uint itemId)
    {
        if (_pendingUnequipFollowup is not { } pending)
        {
            return false;
        }

        if (DateTime.UtcNow - pending.CreatedUtc > PendingUnequipFollowupTtl)
        {
            _pendingUnequipFollowup = null;
            return false;
        }

        if (pending.DestinationSlot != sourceSlot || pending.ItemId != itemId)
        {
            return false;
        }

        _pendingUnequipFollowup = null;
        return true;
    }

}
