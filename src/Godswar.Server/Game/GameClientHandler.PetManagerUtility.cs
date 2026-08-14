using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static PetManagerUtilityOperation ToDurableUtilityOperation(
        PetManagerUtilityRequestOperation operation) =>
        operation switch
        {
            PetManagerUtilityRequestOperation.CheckGrowth =>
                PetManagerUtilityOperation.CheckGrowth,
            PetManagerUtilityRequestOperation.Seal =>
                PetManagerUtilityOperation.Seal,
            PetManagerUtilityRequestOperation.ClaimPetCall =>
                PetManagerUtilityOperation.ClaimPetCall,
            PetManagerUtilityRequestOperation.ClaimMerge =>
                PetManagerUtilityOperation.ClaimMerge,
            PetManagerUtilityRequestOperation.ChangeGender =>
                PetManagerUtilityOperation.ChangeGender,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private async Task<PetDurableReceipt?>
        HandleDurablePetManagerUtilityAsync(
            PetCommandOperationIdentity identity,
            PetManagerUtilityOperation operation,
            int kitBagSlot,
            CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetManagerUtility,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetManagerUtilityCommand(
            identity,
            operation,
            kitBagSlot);
        var unownedEnvelope = identity.IsSecureClient
            ? PetManagerUtilityCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetManagerUtilityCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return null;
        }

        return await ExecuteAndCompletePetCommandAsync(
            identity,
            CommandFamily.PetManagerUtility,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<int> ResolveGenderPreviewResultAsync(
        CancellationToken cancellationToken)
    {
        if (!await RefreshCharacterSnapshotAsync(
                "pet_manager_gender_preview",
                cancellationToken))
        {
            return PetManagerProtocol.GenderUnavailableResultSubId;
        }

        var pet = (_characterLoadSnapshot?.Pets ?? [])
            .SingleOrDefault(static candidate =>
                candidate.IsCarried && candidate.IsSummoned);
        if (pet is null)
        {
            return PetManagerProtocol.GenderNoPetResultSubId;
        }
        if (!pet.IsBound)
        {
            return PetManagerProtocol.GenderUnboundPetResultSubId;
        }
        if (pet.ActivityState != "owned" ||
            pet.ContributesToCharacter)
        {
            return PetManagerProtocol.GenderUnavailableResultSubId;
        }
        return PetManagerProtocol.BuildGenderPreviewSubId(
            pet.Level,
            pet.Sex);
    }

    private async Task<bool> SendPetManagerUtilityProjectionAsync(
        PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition,
        IReadOnlyList<PetBootstrapSnapshot> pets,
        PetBootstrapSnapshot? previousCarriedPet,
        PetManagerUtilityVitals? previousVitals,
        string previousKitBag,
        CancellationToken cancellationToken)
    {
        if (receipt.PetManagerUtility is not { IsValid: true } evidence)
        {
            return false;
        }

        if (receipt.Succeeded &&
            disposition == PetDurableExecutionDisposition.Committed)
        {
            var pet = evidence.PetId > 0
                ? pets.SingleOrDefault(candidate =>
                    candidate.PetId == evidence.PetId)
                : null;
            switch (evidence.Operation)
            {
                case PetManagerUtilityOperation.CheckGrowth:
                    if (pet is null ||
                        !pet.GrowthRevealed ||
                        pet.Revision != receipt.PetRevision)
                    {
                        return false;
                    }
                    break;
                case PetManagerUtilityOperation.Seal:
                    if (pet is not null)
                    {
                        return false;
                    }
                    // The committed pet was the unique summoned target. Tear
                    // down that native model before 10237 rebuilds the owned
                    // list without the now-sealed pet.
                    await _session.SendAsync(
                        PacketBuilder.PetOperationResult(
                            checked((uint)evidence.PetId),
                            PetOperationResultCode.RecallSucceeded),
                        cancellationToken,
                        "DurablePetSealPreviousModelRecall");
                    await PublishOwnedPetUtilityListAsync(
                        pets,
                        cancellationToken,
                        "DurablePetSealListRefresh");
                    if (!await SendPetSkillOwnerStatRefreshAsync(
                            "DurablePetSealCarriedSkillSource",
                            cancellationToken))
                    {
                        return false;
                    }
                    break;
                case PetManagerUtilityOperation.Unseal:
                    if (pet is null || pet.ActivityState != "owned" ||
                        !pet.IsCarried || !pet.IsSummoned ||
                        pet.ContributesToCharacter ||
                        pet.Revision != receipt.PetRevision)
                    {
                        return false;
                    }
                    var character = _character!;
                    var restoredVitals =
                        TryRestoreFullHealthAfterUnseal(
                            previousVitals,
                            character);
                    if (restoredVitals is not null)
                    {
                        if (!await PersistVitalsCheckpointAsync(
                                character,
                                force: true,
                                cancellationToken))
                        {
                            return false;
                        }
                        _registry.UpdateCharacter(
                            _session,
                            character,
                            advanceWorldRevision: false);
                    }
                    if (previousCarriedPet is
                            { IsSummoned: true } previous &&
                        previous.PetId != pet.PetId)
                    {
                        await _session.SendAsync(
                            PacketBuilder.PetOperationResult(
                                checked((uint)previous.PetId),
                                PetOperationResultCode.RecallSucceeded),
                            cancellationToken,
                            "DurablePetUnsealPreviousRecall");
                    }
                    await PublishOwnedPetUtilityListAsync(
                        pets,
                        cancellationToken,
                        "DurablePetUnsealListRefresh");
                    // Unseal is a live in-world selection, like hatch. The
                    // native client must load the new record before applying
                    // Take and Call Out. Opcode 10248 is reserved for the
                    // separate world-ready login/map restore lifecycle.
                    await _session.SendAsync(
                        PacketBuilder.PetOperationResult(
                            checked((uint)pet.PetId),
                            PetOperationResultCode.TakeSucceeded),
                        cancellationToken,
                        "DurablePetUnsealTake");
                    await _session.SendAsync(
                        PacketBuilder.PetOperationResult(
                            checked((uint)pet.PetId),
                            PetOperationResultCode.CallOutSucceeded),
                        cancellationToken,
                        "DurablePetUnsealCallOut");
                    await _session.SendAsync(
                        PacketBuilder.PetEnergy(
                            pet.CurrentEnergy,
                            pet.MaximumEnergy),
                        cancellationToken,
                        "DurablePetUnsealEnergy");
                    if (previousCarriedPet?.PetId != pet.PetId &&
                        !await SendPetSkillOwnerStatRefreshAsync(
                            "DurablePetUnsealCarriedSkillSource",
                            cancellationToken))
                    {
                        return false;
                    }
                    if (restoredVitals is { } vitals)
                    {
                        await _session.SendAsync(
                            PacketBuilder.PlayerVitalsUpdate(
                                LocalPlayerObjectId,
                                vitals.CurrentHp,
                                vitals.CurrentMp),
                            cancellationToken,
                            "DurablePetUnsealFullHealthSelf");
                        await _registry.BroadcastToMapAsync(
                            character.CurrentMap,
                            PacketBuilder.PlayerVitalsUpdate(
                                WorldObjectIds.ForPlayer(character.Id),
                                vitals.CurrentHp,
                                vitals.CurrentMp),
                            cancellationToken,
                            _session,
                            "DurablePetUnsealFullHealthWorld");
                    }
                    break;
                case PetManagerUtilityOperation.ChangeGender:
                    if (pet is null ||
                        pet.Sex != evidence.NewSex ||
                        pet.Revision != receipt.PetRevision)
                    {
                        return false;
                    }
                    await _session.SendAsync(
                        PacketBuilder.PetGenderRefresh(
                            RequirePetContent(),
                            pet),
                        cancellationToken,
                        "DurablePetGenderRefresh");
                    break;
            }
        }

        foreach (var deletion in
                 PacketBuilder.KitBagMutationDeletionAcknowledgements(
                     previousKitBag,
                     _character!.KitBag))
        {
            await _session.SendAsync(
                deletion,
                cancellationToken,
                "DurablePetManagerUtilityBagMutationClear");
        }
        await SendKitBagRefreshAsync(cancellationToken);
        return true;
    }

    private Task PublishOwnedPetUtilityListAsync(
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken,
        string reason) =>
        _session.SendAsync(
            PacketBuilder.OwnedPetList(
                RequirePetContent(),
                pets,
                _characterLoadSnapshot?.PetShed.OpenedCellCount ??
                    PetShedCapacityPolicy.DefaultOpenedCellCount),
            cancellationToken,
            reason);

    private static PetManagerUtilityVitals? CapturePetManagerUtilityVitals(
        GameCharacter? character)
    {
        if (character is null)
        {
            return null;
        }

        lock (character.VitalsSync)
        {
            return new PetManagerUtilityVitals(
                character.CurrentHp,
                character.CurrentMp,
                character.MaxHp,
                character.VitalsRevision);
        }
    }

    private static PetManagerUtilityVitals?
        TryRestoreFullHealthAfterUnseal(
            PetManagerUtilityVitals? previous,
            GameCharacter character)
    {
        if (previous is not { } before)
        {
            return null;
        }

        lock (character.VitalsSync)
        {
            // Increasing a selected pet's Max-HP passive must not turn a
            // full character into an injured one. The revision/current-value
            // fence prevents this projection from healing real damage that
            // raced the durable command. A character already below the old
            // maximum remains at that exact health.
            if (before.CurrentHp <= 0 ||
                before.CurrentHp != before.MaximumHp ||
                character.VitalsRevision != before.Revision ||
                character.CurrentHp != before.CurrentHp ||
                character.MaxHp <= before.MaximumHp)
            {
                return null;
            }

            character.CurrentHp = character.MaxHp;
            character.MarkVitalsChanged();
            return new PetManagerUtilityVitals(
                character.CurrentHp,
                character.CurrentMp,
                character.MaxHp,
                character.VitalsRevision);
        }
    }

    private readonly record struct PetManagerUtilityVitals(
        int CurrentHp,
        int CurrentMp,
        int MaximumHp,
        long Revision);
}
