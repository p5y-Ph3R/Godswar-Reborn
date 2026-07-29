using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
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
            if (_session.IsSecure &&
                packet.ClientOperationId is { } clientOperationId &&
                packet.Length == DurableKitBagDeleteRequestBytes)
            {
                await HandleDurableKitBagItemDeleteAsync(
                    deletedSlot,
                    clientOperationId,
                    cancellationToken);
                return;
            }

            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.KitBagItemDelete);
            await HandleDeleteKitBagItemAsync(deletedSlot, cancellationToken);
            return;
        }

        if (TryReadStorageItemKitBagMove(packet.Payload, out var moveSourceSlot, out var moveDestinationSlot))
        {
            if (packet.ClientOperationId is { } moveOperationId)
            {
                if (_session.IsSecure &&
                    packet.Length is
                        DurableKitBagMoveCompactRequestBytes or
                        DurableKitBagMoveDetailedRequestBytes &&
                    moveSourceSlot != moveDestinationSlot)
                {
                    await HandleDurableKitBagItemMoveAsync(
                        moveSourceSlot,
                        moveDestinationSlot,
                        moveOperationId,
                        cancellationToken);
                    return;
                }

                await RejectUnsupportedDurableKitBagItemMoveAsync(
                    moveOperationId,
                    cancellationToken);
                return;
            }

            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.KitBagItemMove);
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

    private async Task HandleCompatibilityTalentUpgradeAsync(
        LegacyTalentUpgradeCommandAdapter.AdaptedCommand adapted,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var envelope = adapted.Envelope;
        var attempt = _registry.CommandAttempts.TryBegin(
            envelope.OperationId,
            envelope.RequestHash,
            receivedAt);
        if (attempt != CommandAttemptDecision.Accepted)
        {
            var outcome =
                attempt == CommandAttemptDecision.RequestHashConflict
                    ? CommandOutcome.RequestHashConflict
                    : CommandOutcome.Duplicate;
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                outcome);
            return;
        }

        TalentUpgradeResult? result;
        try
        {
            result = await _store.UpgradeTalentAsync(
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                envelope.Command.TalentId,
                envelope.Command.ExpectedRank,
                adapted.ClientTalentPoints,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ReleaseCompatibilityAttempt(envelope);
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch
        {
            ReleaseCompatibilityAttempt(envelope);
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.ProviderUnavailable);
            throw;
        }

        if (result is null)
        {
            ReleaseCompatibilityAttempt(envelope);
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.PreconditionFailed);
            return;
        }

        _registry.CommandAttempts.Complete(
            envelope.OperationId,
            envelope.RequestHash,
            DateTimeOffset.UtcNow);
        CommandMetrics.Record(
            envelope.Family,
            envelope.IdentityStrength,
            CommandOutcome.Accepted);
        _character = result.Character;
        await RefreshActiveCharacterStatsAsync(
            "talent-upgrade",
            cancellationToken);
        _registry.UpdateCharacter(_session, _character);
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PlayerStatusUpdate");
        await _session.SendAsync(
            PacketBuilder.TalentUpgradeAck(result),
            cancellationToken,
            "TalentUpgradeAck");
    }

    private void ReleaseCompatibilityAttempt(
        CommandEnvelope<TalentUpgradeCommand> envelope)
    {
        _registry.CommandAttempts.Release(
            envelope.OperationId,
            envelope.RequestHash);
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
