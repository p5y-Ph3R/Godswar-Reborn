using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> ReloadPetProjectionAsync(
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition,
        CancellationToken cancellationToken)
    {
        var previousKitBag = _character?.KitBag;
        var previousCarriedPet = _characterLoadSnapshot?.Pets
            .SingleOrDefault(static pet => pet.IsCarried);
        var previousVitals = CapturePetManagerUtilityVitals(_character);
        if (!await RefreshCharacterSnapshotAsync(
                "durable_pet_command",
                cancellationToken) ||
            _account is null ||
            _character is null)
        {
            return false;
        }

        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        var pets = _characterLoadSnapshot?.Pets ?? [];

        // Opcode 10237 is not an inert detail refresh: the native client
        // rebuilds its active-pet selection from it and immediately emits a
        // Recall while the pet-unite transition is being processed. Owner
        // Merge therefore has a dedicated lifecycle projection and must
        // never pass through the complete owned-pet-list branch, on either a
        // successful transition or a rejected repeated request.
        if ((receipt.Family is CommandFamily.BagItemActivation or
                CommandFamily.PetOwnerMergeToggle) &&
            IsOwnerMergeReceipt(receipt.Status))
        {
            if (!receipt.Succeeded)
            {
                await SendPetOwnerMergeRejectionProjectionAsync(
                    receipt,
                    pets,
                    cancellationToken);
                return true;
            }

            var receiptPet = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            var activeMerges = pets
                .Where(static pet => pet.ContributesToCharacter)
                .Take(2)
                .ToArray();
            if (activeMerges.Length > 1)
            {
                return false;
            }

            if (disposition == PetDurableExecutionDisposition.Committed)
            {
                if (receiptPet is null)
                {
                    return false;
                }
                var committedMerge = receipt.Status ==
                    PetDurableReceiptStatus.OwnerMerged;
                if (committedMerge != (activeMerges.Length == 1) ||
                    committedMerge &&
                        activeMerges[0].PetId != receipt.PetId)
                {
                    return false;
                }
            }

            // A duplicate receipt describes a historical commit. Project the
            // snapshot's one current contributor, not the historical receipt
            // pet. A delayed receipt for pet A must not tear down a newer
            // Merge owned by pet B.
            if (activeMerges.Length == 1)
            {
                var activePet = activeMerges[0];
                if (!activePet.IsCarried || !activePet.IsSummoned)
                {
                    return false;
                }
                await PublishPetOwnerMergeStartedAsync(
                    activePet,
                    cancellationToken);
            }
            else
            {
                if (receiptPet is null)
                {
                    return false;
                }
                CancelPetOwnerMergeEnergyDrain();
                await PublishPetOwnerMergeEndedAsync(
                    receiptPet,
                    restoreCompanion: true,
                    cancellationToken);
            }
            return true;
        }

        // Native 10269 applies the six exact increments and removes the
        // deputy pet in-place. Sending 10237 here would rebuild carry state
        // and can trigger an unintended Recall, so Merge has a narrow result.
        if (receipt.Family == CommandFamily.PetToPetMerge)
        {
            if (!receipt.Succeeded)
            {
                return true;
            }

            if (disposition == PetDurableExecutionDisposition.Committed)
            {
                var primary = pets.SingleOrDefault(
                    candidate => candidate.PetId == receipt.PetId);
                if (primary is null ||
                    pets.Any(candidate =>
                        candidate.PetId == receipt.DeputyPetId) ||
                    receipt.PetMergeDelta is not { } delta)
                {
                    return false;
                }

                await _session.SendAsync(
                    PacketBuilder.PetToPetMergeResult(
                        receipt.PetId,
                        receipt.DeputyPetId,
                        delta),
                    cancellationToken,
                    "DurablePetToPetMerge");
            }
            else if (!pets.Any(static pet =>
                         pet.ContributesToCharacter))
            {
                // 10269 is additive in the stock client and therefore cannot
                // be replayed safely. A duplicate durable receipt receives a
                // complete authoritative list only while no owner Merge is
                // active. Native 10237 can otherwise auto-Recall that newer
                // owner-Merge state.
                await _session.SendAsync(
                    PacketBuilder.OwnedPetList(
                        RequirePetContent(),
                        pets,
                        _characterLoadSnapshot?.PetShed.OpenedCellCount ??
                            PetShedCapacityPolicy.DefaultOpenedCellCount),
                    cancellationToken,
                    "DurablePetToPetMergeReplayReconcile");
            }
            if (!await SendPetSkillOwnerStatRefreshAsync(
                    "DurablePetToPetMergeRank",
                    cancellationToken))
            {
                return false;
            }
            await SendKitBagRefreshAsync(cancellationToken);
            return true;
        }

        if (receipt.Family == CommandFamily.PetRebirth)
        {
            if (previousKitBag is null)
            {
                return false;
            }
            return await SendPetRebirthProjectionAsync(
                receipt,
                disposition,
                pets,
                previousKitBag,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetSoulContract)
        {
            if (previousKitBag is null)
            {
                return false;
            }
            return await SendPetSoulContractProjectionAsync(
                receipt,
                disposition,
                pets,
                previousKitBag,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetAppearanceChange)
        {
            if (previousKitBag is null)
            {
                return false;
            }
            return await SendPetAppearanceChangeProjectionAsync(
                receipt,
                disposition,
                pets,
                previousKitBag,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetBind)
        {
            return await SendPetBindProjectionAsync(
                receipt,
                disposition,
                pets,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.PetManagerUtility)
        {
            if (previousKitBag is null)
            {
                return false;
            }
            return await SendPetManagerUtilityProjectionAsync(
                receipt,
                disposition,
                pets,
                previousCarriedPet,
                previousVitals,
                previousKitBag,
                cancellationToken);
        }

        if (receipt.Family == CommandFamily.BagItemActivation)
        {
            if (receipt.Status is
                    PetDurableReceiptStatus.PetShedExpanded or
                    PetDurableReceiptStatus.PetShedMaximumReached)
            {
                await _session.SendAsync(
                    PacketBuilder.PetShedExpansionResult(
                        receipt.Status ==
                            PetDurableReceiptStatus.PetShedExpanded
                            ? PetShedExpansionResultCode.Succeeded
                            : PetShedExpansionResultCode.AlreadyMaximum),
                    cancellationToken,
                    "DurablePetShedExpansionResult");
            }

            // A detail refresh cannot evict a now-empty item object. Clear
            // only an authoritatively empty committed slot, preserving the
            // stock cooling overlay for remaining stacks.
            if (receipt.Succeeded &&
                receipt.KitBagSlot >= 0 &&
                KitBagSlots.GetItem(
                    _character.KitBag,
                    receipt.KitBagSlot).IsEmpty)
            {
                await _session.SendAsync(
                    PacketBuilder.StorageItemKitBagDelete(
                        receipt.KitBagSlot),
                    cancellationToken,
                    "DurablePetBagActivationSlotClear");
            }
            await SendKitBagRefreshAsync(cancellationToken);
            if (receipt.EquipmentSlot >= 0)
            {
                var equipment = PacketBuilder.EquipmentItemSnapshot(
                    _character,
                    receipt.EquipmentSlot);
                if (equipment.Length == 0)
                {
                    equipment = PacketBuilder
                        .EquipmentItemClearSnapshot(
                            receipt.EquipmentSlot);
                }
                await _session.SendAsync(
                    equipment,
                    cancellationToken,
                    "DurablePetEquipmentRefresh");
                await _session.SendAsync(
                    PacketBuilder.EquipmentVisualRefresh(
                        _character,
                        _itemContent?.FashionAppearances),
                    cancellationToken,
                    "DurablePetEquipmentVisualRefresh");
                await _session.SendAsync(
                    PacketBuilder.EquipmentEffectVisibility(
                        LocalPlayerObjectId,
                        ResolveEquipmentEffectProjection(_character)),
                    cancellationToken,
                    "DurablePetEquipmentEffectVisibility");
                await BroadcastEquipmentRefreshAsync(
                    "durable_bag_activation",
                    cancellationToken);
            }
            PetBootstrapSnapshot? hatched = null;
            PetBootstrapSnapshot? skillCellPet = null;
            PetBootstrapSnapshot? experiencePet = null;
            if (receipt.Status == PetDurableReceiptStatus.EggHatched)
            {
                hatched = pets.SingleOrDefault(
                    candidate => candidate.PetId == receipt.PetId);
                if (hatched is null ||
                    !hatched.IsCarried ||
                    hatched.IsSummoned != receipt.IsSummoned)
                {
                    return false;
                }

                if (previousCarriedPet is
                        { IsSummoned: true } previous &&
                    previous.PetId != hatched.PetId)
                {
                    await _session.SendAsync(
                        PacketBuilder.PetOperationResult(
                            checked((uint)previous.PetId),
                            PetOperationResultCode.RecallSucceeded),
                        cancellationToken,
                        "DurablePetHatchPreviousRecall");
                }
            }
            else if (receipt.Status is
                         PetDurableReceiptStatus
                             .PetSkillCellMadeAvailable or
                         PetDurableReceiptStatus.PetSkillCellOpened or
                         PetDurableReceiptStatus.PetSkillLearned)
            {
                skillCellPet = pets.SingleOrDefault(
                    candidate => candidate.PetId == receipt.PetId);
                if (skillCellPet is null ||
                    skillCellPet.Revision != receipt.PetRevision)
                {
                    return false;
                }
            }
            else if (receipt.Status ==
                     PetDurableReceiptStatus.PetExperienceAdded)
            {
                experiencePet = pets.SingleOrDefault(
                    candidate => candidate.PetId == receipt.PetId);
                if (experiencePet is null ||
                    !experiencePet.IsCarried ||
                    experiencePet.Experience != receipt.PetExperience ||
                    experiencePet.Revision != receipt.PetRevision)
                {
                    return false;
                }
            }

            // Native 10237 rebuilds active-pet selection. Skill-cell items
            // instead use 10247 and must preserve carry/summon presentation.
            if (skillCellPet is not null)
            {
                await _session.SendAsync(
                    PacketBuilder.PetSkillState(skillCellPet),
                    cancellationToken,
                    "DurablePetSkillStateRefresh");
                if (receipt.Status ==
                        PetDurableReceiptStatus.PetSkillLearned &&
                    !await SendPetSkillOwnerStatRefreshAsync(
                        "DurablePetSkillLearned",
                        cancellationToken))
                {
                    return false;
                }
            }
            else if (experiencePet is not null)
            {
                // Opcode 10237 rebuilds native carry/summon state and can
                // auto-recall an actively merged pet. Morning Dew has its
                // own narrow original-server EXP projection.
                await _session.SendAsync(
                    PacketBuilder.PetExperience(
                        experiencePet.PetId,
                        experiencePet.Experience),
                    cancellationToken,
                    "DurablePetExperienceRefresh");
            }
            else
            {
                await _session.SendAsync(
                    PacketBuilder.OwnedPetList(
                        RequirePetContent(),
                        pets,
                        _characterLoadSnapshot?.PetShed.OpenedCellCount ??
                            PetShedCapacityPolicy.DefaultOpenedCellCount),
                    cancellationToken,
                    "DurablePetListRefresh");
            }
            if (hatched is not null)
            {
                await _session.SendAsync(
                    PacketBuilder.PetOperationResult(
                        checked((uint)hatched.PetId),
                        PetOperationResultCode.TakeSucceeded),
                    cancellationToken,
                    "DurablePetHatchTake");
                if (hatched.IsSummoned)
                {
                    await _session.SendAsync(
                        PacketBuilder.PetOperationResult(
                            checked((uint)hatched.PetId),
                            PetOperationResultCode.CallOutSucceeded),
                        cancellationToken,
                        "DurablePetHatchCallOut");
                }
                if (previousCarriedPet?.PetId != hatched.PetId &&
                    !await SendPetSkillOwnerStatRefreshAsync(
                        "DurablePetHatchCarriedSkillSource",
                        cancellationToken))
                {
                    return false;
                }
            }
        }
        else if (receipt.Family == CommandFamily.PetSkillUnlearn &&
                 receipt.Succeeded)
        {
            return await SendPetSkillUnlearnProjectionAsync(
                receipt,
                pets,
                cancellationToken);
        }
        else if (receipt.Family == CommandFamily.PetGrowthReset &&
                 receipt.Succeeded)
        {
            return await SendPetGrowthResetProjectionAsync(
                receipt,
                pets,
                cancellationToken);
        }
        else if (receipt.Family == CommandFamily.PetBasicSavvyReset &&
                 receipt.Succeeded)
        {
            return await SendPetBasicSavvyResetProjectionAsync(
                receipt,
                pets,
                cancellationToken);
        }
        else if (receipt.Family == CommandFamily.PetLevelUpgrade &&
                 receipt.Succeeded)
        {
            var pet = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            if (pet is null)
            {
                return false;
            }
            return await SendPetProgressionRefreshAsync(
                pet,
                "DurablePetLevelUpgrade",
                cancellationToken);
        }
        else if (receipt.Family ==
                 CommandFamily.PetPresenceTransition)
        {
            var target = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            var result = ResolveAuthoritativePresenceResult(
                receipt,
                target is not null,
                target?.IsCarried == true,
                target?.IsSummoned == true);
            await _session.SendAsync(
                PacketBuilder.PetOperationResult(
                    checked((uint)receipt.PetId),
                    result),
                cancellationToken,
                "DurablePetPresenceResult");

            // Take atomically selects and summons. Preserve the native Take,
            // then CallOut order so the old model is disposed before spawn.
            if (receipt.Succeeded &&
                receipt.PresenceOperation ==
                    checked((byte)(
                        (byte)PetPresenceCommandOperation.Take + 1)) &&
                result == PetOperationResultCode.TakeSucceeded &&
                target is { IsCarried: true, IsSummoned: true } &&
                previousCarriedPet?.PetId != target.PetId)
            {
                await _session.SendAsync(
                    PacketBuilder.PetOperationResult(
                        checked((uint)target.PetId),
                        PetOperationResultCode.CallOutSucceeded),
                    cancellationToken,
                    "DurablePetTakeAutoCallOut");
            }
            if (receipt.Succeeded &&
                receipt.PresenceOperation ==
                    checked((byte)(
                        (byte)PetPresenceCommandOperation.Take + 1)) &&
                pets.SingleOrDefault(static pet => pet.IsCarried) is
                    { } carried &&
                previousCarriedPet?.PetId != carried.PetId &&
                !await SendPetSkillOwnerStatRefreshAsync(
                    "DurablePetTakeCarriedSkillSource",
                    cancellationToken))
            {
                return false;
            }
            if (receipt.Succeeded &&
                target is { ContributesToCharacter: false })
            {
                StartPetOwnerMergeEnergyRecharge();
            }
        }

        return true;
    }

    private static bool IsOwnerMergeReceipt(
        PetDurableReceiptStatus status) =>
        status is >= PetDurableReceiptStatus.OwnerMerged and
            <= PetDurableReceiptStatus.OwnerMergeCharmInvalid;
}
