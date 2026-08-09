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
    private async Task HandleEquipItemAsync(
        int sourceSlot,
        int requestedEquipmentSlot,
        uint itemIdHint,
        CancellationToken cancellationToken,
        bool sendStorageTransferAck = false)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (!AllowLegacyPlayerMutationFallback(
                "equipment_bag_transfer"))
        {
            return;
        }

        if (sourceSlot is < 0 or >= 96)
        {
            Console.WriteLine($"[equip-re] StorageItem equip ignored: unsupported sourceSlot={sourceSlot}");
            return;
        }

        var previousEquipment = _character.Equipment;
        var previousKitBagEntry = KitBagSlots.GetEntry(_character.KitBag, sourceSlot);
        var kitBagItemId = KitBagSlots.GetItemId(_character.KitBag, sourceSlot);
        if (kitBagItemId == 0)
        {
            Console.WriteLine($"[equip-re] StorageItem equip ignored: empty sourceSlot={sourceSlot}");
            await SendEquipRejectionRefreshAsync(
                requestedEquipmentSlot,
                resolvedEquipmentSlot: -1,
                sourceSlot,
                cancellationToken);
            return;
        }

        if (itemIdHint != 0 && itemIdHint != kitBagItemId)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem equip ignored: stale item sourceSlot={sourceSlot} hint={itemIdHint} actual={kitBagItemId}");
            await SendEquipRejectionRefreshAsync(
                requestedEquipmentSlot,
                resolvedEquipmentSlot: -1,
                sourceSlot,
                cancellationToken);
            return;
        }

        var effectiveItemIdHint = kitBagItemId;
        if (!EquipmentSlots.TryGetAuthoritativeSlot(
                RequireItemContent().Templates,
                effectiveItemIdHint,
                out var authoritativeEquipmentSlot))
        {
            Console.WriteLine(
                $"[equip-re] StorageItem equip rejected: item={effectiveItemIdHint} has no equipment slot");
            await SendEquipRejectionRefreshAsync(
                requestedEquipmentSlot,
                resolvedEquipmentSlot: -1,
                sourceSlot,
                cancellationToken);
            return;
        }

        var resolvedEquipmentSlot = EquipmentSlots.ResolveSlotForItem(
            effectiveItemIdHint,
            requestedEquipmentSlot,
            _character.Equipment,
            _character.Profession,
            authoritativeEquipmentSlot);
        var equipEligibility = EquipmentEligibility.ValidateEquip(
            RequireItemContent(),
            _character.Profession,
            _character.Level,
            _character.Equipment,
            effectiveItemIdHint,
            resolvedEquipmentSlot);
        if (!equipEligibility.Allowed)
        {
            Console.WriteLine(
                $"[mount] rejected equip character={_character.Name} item={effectiveItemIdHint} slot={resolvedEquipmentSlot}: {equipEligibility.Reason}");
            await SendEquipRejectionRefreshAsync(
                requestedEquipmentSlot,
                resolvedEquipmentSlot,
                sourceSlot,
                cancellationToken);

            return;
        }

        if (resolvedEquipmentSlot == EquipmentSlots.Mount &&
            (IsSkillCastPending(MountCatalog.RideSkillId) ||
             _registry.IsRuntimeStatusActive(
                 _session,
                 MountCatalog.RuntimeStatusKind,
                 DateTimeOffset.UtcNow)))
        {
            Console.WriteLine(
                $"[mount] rejected mount replacement while Ride is active or pending character={_character.Name} item={effectiveItemIdHint}");
            await SendEquipRejectionRefreshAsync(
                requestedEquipmentSlot,
                resolvedEquipmentSlot,
                sourceSlot,
                cancellationToken);

            return;
        }

        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.MoveKitBagToEquipment);
        var updatedCharacter = await _store.MoveKitBagToEquipmentAsync(
            _account.Id,
            _character.Id,
            sourceSlot,
            requestedEquipmentSlot,
            cancellationToken,
            requireEmptyEquipmentSlot: sendStorageTransferAck);

        if (updatedCharacter is null)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem equip failed: character={_character.Name} id={_character.Id} sourceSlot={sourceSlot}");
            await SendEquipRejectionRefreshAsync(
                requestedEquipmentSlot,
                resolvedEquipmentSlot,
                sourceSlot,
                cancellationToken);
            return;
        }

        if (string.Equals(
                KitBagSlots.GetEntry(updatedCharacter.KitBag, sourceSlot),
                previousKitBagEntry,
                StringComparison.Ordinal))
        {
            InstallUpdatedCharacter(updatedCharacter);
            Console.WriteLine(
                $"[equip-re] StorageItem equip did not move item: character={_character.Name} sourceSlot={sourceSlot} requestedTarget={requestedEquipmentSlot} item={kitBagItemId}");
            await SendEquipRejectionRefreshAsync(
                requestedEquipmentSlot,
                resolvedEquipmentSlot,
                sourceSlot,
                cancellationToken);

            return;
        }

        InstallUpdatedCharacter(updatedCharacter);
        await RefreshActiveCharacterStatsAsync("equip", cancellationToken);
        _registry.UpdateCharacter(_session, _character);
        var equippedSlot = ResolveEquippedSlotForAck(
            _character,
            previousEquipment,
            requestedEquipmentSlot,
            effectiveItemIdHint);
        Console.WriteLine(
            $"[equip-re] equipped character={_character.Name} sourceSlot={sourceSlot} requestedTarget={requestedEquipmentSlot} equippedSlot={equippedSlot} itemHint={effectiveItemIdHint} equipment={PacketBuilder.EnterEquipmentSummary(_character)}");

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PlayerStatusUpdate");
        if (sendStorageTransferAck && EquipmentSlots.IsEquipmentSlot(equippedSlot))
        {
            await _session.SendAsync(
                PacketBuilder.StorageItemEquipmentBagTransfer(
                    PacketBuilder.ToClientEquipmentSlot(equippedSlot),
                    sourceSlot),
                cancellationToken,
                "StorageItemEquipmentBagTransferAck");
        }

        if (!sendStorageTransferAck)
        {
            var snapshot = PacketBuilder.EquipmentItemEquipSnapshot(_character, sourceSlot, equippedSlot);
            if (snapshot.Length > 0)
            {
                await _session.SendAsync(
                    snapshot,
                    cancellationToken,
                    "EquipmentItemSnapshot");
            }
        }

        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(
                _character,
                _itemContent?.FashionAppearances),
            cancellationToken,
            "EquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                LocalPlayerObjectId,
                ResolveEquipmentEffectProjection(_character)),
            cancellationToken,
            "EquipmentEffectVisibility");
        await BroadcastEquipmentRefreshAsync("equip", cancellationToken);
    }

    private async Task HandleMoveKitBagItemAsync(int sourceSlot, int destinationSlot, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (!AllowLegacyPlayerMutationFallback(
                "kit_bag_item_move"))
        {
            return;
        }

        if (sourceSlot is < 0 or >= 96 || destinationSlot is < 0 or >= 96)
        {
            Console.WriteLine($"[equip-re] StorageItem kitbag move ignored: unsupported source={sourceSlot} destination={destinationSlot}");
            return;
        }

        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.MoveKitBagItem);
        var updatedCharacter = await _store.MoveKitBagItemAsync(
            _account.Id,
            _character.Id,
            sourceSlot,
            destinationSlot,
            cancellationToken);

        if (updatedCharacter is null)
        {
            Console.WriteLine(
                $"[equip-re] StorageItem kitbag move failed: character={_character.Name} id={_character.Id} source={sourceSlot} destination={destinationSlot}");
            return;
        }

        InstallUpdatedCharacter(updatedCharacter);
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        Console.WriteLine(
            $"[equip-re] kitbag move character={_character.Name} source={sourceSlot} destination={destinationSlot}");

        await _session.SendAsync(
            PacketBuilder.StorageItemKitBagMove(sourceSlot, destinationSlot),
            cancellationToken,
            "StorageItemKitBagMoveAck");

        await _session.SendAsync(
            PacketBuilder.KitBagSlotIndex(_character, sourceSlot),
            cancellationToken,
            "StorageItemKitBagSourceRefresh");
        if (destinationSlot != sourceSlot)
        {
            await _session.SendAsync(
                PacketBuilder.KitBagSlotIndex(_character, destinationSlot),
                cancellationToken,
                "StorageItemKitBagDestinationRefresh");
        }
    }

    private async Task HandleDeleteKitBagItemAsync(int sourceSlot, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (!AllowLegacyPlayerMutationFallback(
                "kit_bag_item_delete"))
        {
            return;
        }

        var itemId = KitBagSlots.GetItemId(_character.KitBag, sourceSlot);
        if (itemId == 0)
        {
            Console.WriteLine($"[inventory] kitbag delete ignored: empty source={sourceSlot}");
            return;
        }

        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.DeleteKitBagItem);
        var updatedCharacter = await _store.DeleteKitBagItemAsync(
            _account.Id,
            _character.Id,
            sourceSlot,
            cancellationToken);

        if (updatedCharacter is null
            || KitBagSlots.GetItemId(updatedCharacter.KitBag, sourceSlot) == itemId)
        {
            Console.WriteLine(
                $"[inventory] kitbag delete failed: character={_character.Name} id={_character.Id} source={sourceSlot} item={itemId}");
            return;
        }

        InstallUpdatedCharacter(updatedCharacter);
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        Console.WriteLine(
            $"[inventory] deleted kitbag item character={_character.Name} source={sourceSlot} item={itemId}");
        await _session.SendAsync(
            PacketBuilder.StorageItemKitBagDelete(sourceSlot),
            cancellationToken,
            "StorageItemKitBagDeleteAck");
    }

}
