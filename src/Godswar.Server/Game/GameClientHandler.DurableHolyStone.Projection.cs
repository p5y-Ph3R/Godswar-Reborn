using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task ReloadDurableHolyStoneProjectionAsync(
        HolyStoneExecutionReceipt? committedReceipt,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            _processRealmId,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The Holy Stone owner changed during projection reload.");
        }

        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable Holy Stone character could not be reloaded.");
        }
        if (committedReceipt is not null)
        {
            ValidateCommittedHolyStoneProjection(
                hydrated.Character,
                committedReceipt);
            if (committedReceipt.Operation is (
                    HolyStoneCommandOperation.Drill or
                    HolyStoneCommandOperation.MountGearDrill) &&
                committedReceipt.Status ==
                    HolyStoneCommandResultStatus.Drilled &&
                hydrated.Character.Gold !=
                    committedReceipt.GoldAfter)
            {
                throw new InvalidDataException(
                    "The committed Holy Stone Gold projection is stale.");
            }
        }

        // Holy Stone mutations can affect equipped stats or a bag item.
        // Reuse the equipment projection's live-vitals preservation rather
        // than importing a potentially older persisted HP/MP snapshot.
        ApplyDurableEquipmentBagTransferProjection(
            _character,
            hydrated.Character);
        _character.Gold = hydrated.Character.Gold;
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
    }

    private static void ValidateCommittedHolyStoneProjection(
        GameCharacter persistedCharacter,
        HolyStoneExecutionReceipt receipt)
    {
        var projectedTarget = receipt.TargetLocation switch
        {
            HolyStoneTargetLocation.Equipment =>
                EquipmentSlots.GetItem(
                    persistedCharacter.Equipment,
                    persistedCharacter.Profession,
                    receipt.TargetSlot),
            HolyStoneTargetLocation.KitBag =>
                KitBagSlots.GetItem(
                    persistedCharacter.KitBag,
                    receipt.TargetSlot),
            _ => CompactItemEntry.Empty
        };
        if (!string.Equals(
                projectedTarget.ToCompactString(),
                receipt.AuthoritativeTargetAfterCompactItemState,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The committed Holy Stone target projection is stale.");
        }

        if (receipt.Operation is
                HolyStoneCommandOperation.Mount or
                HolyStoneCommandOperation.AdvancedDrill or
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit)
        {
            var projectedStone = KitBagSlots.GetItem(
                persistedCharacter.KitBag,
                receipt.StoneKitBagSlot);
            if (!string.Equals(
                    projectedStone.ToCompactString(),
                    receipt.AuthoritativeStoneAfterCompactItemState,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The committed Holy Stone material projection is " +
                    "stale.");
            }

            if (receipt.Operation is
                    HolyStoneCommandOperation.Upgrade or
                    HolyStoneCommandOperation.ImplementSpirit &&
                receipt.CatalystKitBagSlot >=
                    HolyStoneCommandEnvelope.MinimumKitBagSlot)
            {
                var projectedCatalyst = KitBagSlots.GetItem(
                    persistedCharacter.KitBag,
                    receipt.CatalystKitBagSlot);
                if (!string.Equals(
                        projectedCatalyst.ToCompactString(),
                        receipt.AuthoritativeCatalystAfterCompactItemState,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The committed Holy Stone catalyst projection is " +
                        "stale.");
                }
            }
            if (receipt.Operation == HolyStoneCommandOperation.Combine)
            {
                var evidence = receipt.CombinationEvidence ??
                    throw new InvalidDataException(
                        "The Combination receipt has no third material evidence.");
                var projectedSecondMaterial = KitBagSlots.GetItem(
                    persistedCharacter.KitBag,
                    receipt.CatalystKitBagSlot);
                var projectedThirdMaterial = KitBagSlots.GetItem(
                    persistedCharacter.KitBag,
                    evidence.ThirdMaterialKitBagSlot);
                if (!string.Equals(
                        projectedSecondMaterial.ToCompactString(),
                        receipt.AuthoritativeCatalystAfterCompactItemState,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        projectedThirdMaterial.ToCompactString(),
                        evidence
                            .AuthoritativeThirdMaterialAfterCompactItemState,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The committed Combination material projection is stale.");
                }
            }
        }
        else if (receipt.Operation ==
            HolyStoneCommandOperation.Remove)
        {
            var projectedOutput = KitBagSlots.GetItem(
                persistedCharacter.KitBag,
                receipt.OutputKitBagSlot);
            if (!string.Equals(
                    projectedOutput.ToCompactString(),
                    receipt.OutputAfterCompactItemState,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The committed removed-stone projection is stale.");
            }
        }
    }

    private static void ValidateHolyStoneReceiptIdentity(
        int characterId,
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        HolyStoneExecutionReceipt receipt)
    {
        if (receipt.CharacterId != characterId ||
            receipt.Operation != intent.Operation ||
            receipt.Family != HolyStoneProtocol.Family(intent.Operation) ||
            !HolyStoneCommandEnvelope.AreEquivalentEndpoints(
                receipt.NpcId,
                receipt.DialogIndex,
                checked((int)npcId),
                dialogIndex) ||
            receipt.TargetLocation != intent.TargetLocation ||
            receipt.TargetSlot != intent.TargetSlot ||
            receipt.StoneKitBagSlot != intent.StoneKitBagSlot ||
            receipt.CatalystKitBagSlot != intent.CatalystKitBagSlot ||
            intent.Operation == HolyStoneCommandOperation.Combine &&
            (receipt.CombinationEvidence is not { } combinationEvidence ||
             combinationEvidence.ThirdMaterialKitBagSlot !=
                intent.ThirdMaterialKitBagSlot) ||
            intent.Operation == HolyStoneCommandOperation.Remove &&
            receipt.SocketIndex != intent.SocketIndex)
        {
            throw new InvalidDataException(
                "The Holy Stone receipt identity does not match the " +
                "active command.");
        }
    }

    private async Task SendDurableHolyStoneReceiptAsync(
        uint responseNpcId,
        int responseDialogIndex,
        Guid clientOperationId,
        HolyStoneExecutionReceipt receipt,
        HolyStoneExecutionDisposition disposition,
        string kitBagBeforeExecution,
        CancellationToken cancellationToken)
    {
        var isUpgrade = receipt.Operation ==
            HolyStoneCommandOperation.Upgrade;
        var isCombination = receipt.Operation ==
            HolyStoneCommandOperation.Combine;
        var isImplementation = receipt.Operation ==
            HolyStoneCommandOperation.ImplementSpirit;
        if (!isUpgrade && !isCombination && !isImplementation)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    responseNpcId,
                    responseDialogIndex,
                    receipt.NativeResultSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
        }

        foreach (var acknowledgement in
            PacketBuilder.KitBagMutationDeletionAcknowledgements(
                kitBagBeforeExecution,
                _character!.KitBag))
        {
            await _session.SendAsync(
                acknowledgement,
                cancellationToken,
                "HolyStoneKitBagDeleteAck");
        }

        await SendHolyStoneAuthoritativeProjectionAsync(
            receipt.Status.ToString(),
            cancellationToken);
        var secureDisposition = disposition switch
        {
            HolyStoneExecutionDisposition.Committed =>
                SecureLegacyCommandDisposition.Applied,
            HolyStoneExecutionDisposition.Duplicate =>
                SecureLegacyCommandDisposition.Replayed,
            _ => SecureLegacyCommandDisposition.Rejected
        };
        if (isCombination)
        {
            // The shim must observe the durable settlement before the
            // stock 3300 result panel is exposed, otherwise it cannot bind
            // that legacy panel to the completed operation.
            await SendSecureHolyStoneResultAsync(
                clientOperationId,
                receipt.Family,
                receipt.NativeResultSubId,
                secureDisposition,
                receipt.InventoryRevision,
                cancellationToken);
        }
        if (isUpgrade)
        {
            await SendHolyStoneUpgradeResultPanelAsync(
                responseNpcId,
                responseDialogIndex,
                receipt.NativeResultSubId,
                cancellationToken);
        }
        else if (isImplementation)
        {
            await SendHolySpiritImplementationResultPanelAsync(
                responseNpcId,
                responseDialogIndex,
                receipt.NativeResultSubId,
                cancellationToken);
        }
        else if (isCombination)
        {
            await SendHolyStoneCombinationResultPanelAsync(
                responseNpcId,
                responseDialogIndex,
                receipt.NativeResultSubId,
                cancellationToken);
        }
        if (!isCombination)
        {
            await SendSecureHolyStoneResultAsync(
                clientOperationId,
                receipt.Family,
                receipt.NativeResultSubId,
                secureDisposition,
                receipt.InventoryRevision,
                cancellationToken);
        }
    }

    private async Task SendHolyStoneProjectionAndResultAsync(
        uint npcId,
        int dialogIndex,
        Guid clientOperationId,
        Godswar.Server.Application.Commands.CommandFamily family,
        int nativeResultSubId,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        string kitBagBeforeExecution,
        CancellationToken cancellationToken)
    {
        var isUpgrade = family == CommandFamily.HolyStoneUpgrade;
        var isCombination = family == CommandFamily.HolyStoneCombine;
        var isImplementation = family ==
            CommandFamily.HolyStoneImplementSpirit;
        if (!isUpgrade && !isCombination && !isImplementation)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    nativeResultSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
        }
        foreach (var acknowledgement in
            PacketBuilder.KitBagMutationDeletionAcknowledgements(
                kitBagBeforeExecution,
                _character!.KitBag))
        {
            await _session.SendAsync(
                acknowledgement,
                cancellationToken,
                "HolyStoneKitBagDeleteAck");
        }
        await SendHolyStoneAuthoritativeProjectionAsync(
            "rejected",
            cancellationToken);
        if (isCombination)
        {
            await SendSecureHolyStoneResultAsync(
                clientOperationId,
                family,
                nativeResultSubId,
                disposition,
                inventoryRevision,
                cancellationToken);
        }
        if (isUpgrade)
        {
            await SendHolyStoneUpgradeResultPanelAsync(
                npcId,
                dialogIndex,
                nativeResultSubId,
                cancellationToken);
        }
        else if (isImplementation)
        {
            await SendHolySpiritImplementationResultPanelAsync(
                npcId,
                dialogIndex,
                nativeResultSubId,
                cancellationToken);
        }
        else if (isCombination)
        {
            await SendHolyStoneCombinationResultPanelAsync(
                npcId,
                dialogIndex,
                nativeResultSubId,
                cancellationToken);
        }
        if (!isCombination)
        {
            await SendSecureHolyStoneResultAsync(
                clientOperationId,
                family,
                nativeResultSubId,
                disposition,
                inventoryRevision,
                cancellationToken);
        }
    }

    private async Task SendHolyStoneAuthoritativeProjectionAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "HolyStonePlayerStatus");

        var weapon = PacketBuilder.EquipmentItemSnapshot(
            _character!,
            EquipmentSlots.Weapon);
        if (weapon.Length == 0)
        {
            weapon = PacketBuilder.EquipmentItemClearSnapshot(
                EquipmentSlots.Weapon);
        }
        await _session.SendAsync(
            weapon,
            cancellationToken,
            "HolyStoneWeaponRefresh");
        await SendKitBagRefreshAsync(cancellationToken);
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(
                _character!,
                _itemContent?.FashionAppearances),
            cancellationToken,
            "HolyStoneVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                LocalPlayerObjectId,
                ResolveEquipmentEffectProjection(_character!)),
            cancellationToken,
            "HolyStoneEquipmentEffectVisibility");
        await BroadcastEquipmentRefreshAsync(
            $"holy-stone-{reason}",
            cancellationToken);
    }

    private ValueTask SendSecureHolyStoneResultAsync(
        Guid clientOperationId,
        Godswar.Server.Application.Commands.CommandFamily family,
        int nativeResultSubId,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            throw new InvalidOperationException(
                "Holy Stone operation identity requires secure transport.");
        }

        return SendSecureGearMentorResultAsync(
            clientOperationId,
            family,
            nativeResultSubId,
            disposition,
            inventoryRevision,
            cancellationToken);
    }
}
