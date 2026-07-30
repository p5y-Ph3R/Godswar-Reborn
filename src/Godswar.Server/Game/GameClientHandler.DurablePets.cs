using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IPetDurableCommandExecutor? _petDurableCommands;

    private async Task HandleDurableBagItemActivationAsync(
        Guid operationId,
        int kitBagSlot,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.BagItemActivation,
                operationId,
                "provider or active character is unavailable");
            return;
        }

        var unownedEnvelope = BagItemActivationCommandEnvelope.Create(
            subject,
            SecurePetCorrelation(),
            DateTimeOffset.UtcNow,
            new BagItemActivationCommand(
                operationId,
                kitBagSlot,
                IsSkillCastPending(MountCatalog.RideSkillId) ||
                _registry.IsRuntimeStatusActive(
                    _session,
                    MountCatalog.RuntimeStatusKind,
                    DateTimeOffset.UtcNow)
                    ? BagItemActivationExecutionConstraint
                        .RideRuntimeBlocked
                    : BagItemActivationExecutionConstraint.None));
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        await ExecuteAndCompletePetCommandAsync(
            operationId,
            CommandFamily.BagItemActivation,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task HandleDurablePetLevelUpgradeAsync(
        Guid operationId,
        long petId,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetLevelUpgrade,
                operationId,
                "provider or active character is unavailable");
            return;
        }

        var unownedEnvelope = PetLevelUpgradeCommandEnvelope.Create(
            subject,
            SecurePetCorrelation(),
            DateTimeOffset.UtcNow,
            new PetLevelUpgradeCommand(operationId, petId));
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        await ExecuteAndCompletePetCommandAsync(
            operationId,
            CommandFamily.PetLevelUpgrade,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task HandleDurablePetPresenceAsync(
        Guid operationId,
        long petId,
        PetPresenceOperation operation,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetPresenceTransition,
                operationId,
                "provider or active character is unavailable");
            return;
        }

        var unownedEnvelope =
            PetPresenceTransitionCommandEnvelope.Create(
            subject,
            SecurePetCorrelation(),
            DateTimeOffset.UtcNow,
            new PetPresenceTransitionCommand(
                operationId,
                petId,
                ToPetPresenceCommandOperation(operation)));
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        await ExecuteAndCompletePetCommandAsync(
            operationId,
            CommandFamily.PetPresenceTransition,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private static PetPresenceCommandOperation
        ToPetPresenceCommandOperation(PetPresenceOperation operation) =>
        operation switch
        {
            PetPresenceOperation.Take =>
                PetPresenceCommandOperation.Take,
            PetPresenceOperation.CallOut =>
                PetPresenceCommandOperation.CallOut,
            PetPresenceOperation.Recall =>
                PetPresenceCommandOperation.Recall,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private async Task ExecuteAndCompletePetCommandAsync(
        Guid operationId,
        CommandFamily family,
        Godswar.Server.Application.Characters.PlayerOwnershipFence
            ownership,
        Func<Task<PetDurableExecutionResult>> execute,
        CancellationToken cancellationToken)
    {
        PetDurableExecutionResult execution;
        try
        {
            execution = await execute();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            RecordPetProviderUnavailable(
                family,
                operationId,
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        if (execution.Receipt is { } receipt)
        {
            if (!await ReloadPetProjectionAsync(
                    receipt,
                    cancellationToken))
            {
                RecordPetProviderUnavailable(
                    family,
                    operationId,
                    "committed projection could not be reloaded");
                return;
            }

            await SendPetLegacyResultAsync(
                operationId,
                receipt,
                execution.Disposition,
                cancellationToken);
            return;
        }

        if (execution.Disposition is
                PetDurableExecutionDisposition.RequestHashConflict or
                PetDurableExecutionDisposition.InvalidIntent or
                PetDurableExecutionDisposition.CharacterNotFound)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                execution.Disposition ==
                    PetDurableExecutionDisposition.RequestHashConflict
                    ? CommandOutcome.RequestHashConflict
                    : CommandOutcome.InvalidIntent);
            await _session.SendLegacyCommandResultAsync(
                new SecureLegacyCommandResult(
                    execution.Disposition ==
                        PetDurableExecutionDisposition
                            .RequestHashConflict
                        ? SecureLegacyCommandDisposition.Conflict
                        : SecureLegacyCommandDisposition.Rejected,
                    (ushort)family,
                    0,
                    0,
                    operationId),
                cancellationToken);
            return;
        }

        RecordPetProviderUnavailable(
            family,
            operationId,
            $"unresolved execution {execution.Disposition}");
    }

    private async Task<bool> ReloadPetProjectionAsync(
        PetDurableReceipt receipt,
        CancellationToken cancellationToken)
    {
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

        if (receipt.Family == CommandFamily.BagItemActivation)
        {
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
                    PacketBuilder.EquipmentVisualRefresh(_character),
                    cancellationToken,
                    "DurablePetEquipmentVisualRefresh");
                await BroadcastEquipmentRefreshAsync(
                    "durable_bag_activation",
                    cancellationToken);
            }
            await _session.SendAsync(
                PacketBuilder.OwnedPetList(pets),
                cancellationToken,
                "DurablePetListRefresh");
        }
        else if (receipt.Family == CommandFamily.PetLevelUpgrade &&
                 receipt.Succeeded)
        {
            var pet = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            if (pet is null || pet.StatValues.Count != 6)
            {
                return false;
            }
            var values = pet.StatValues
                .OrderBy(static stat => stat.StatCode)
                .Select(static stat => stat.InitialSavvy)
                .ToArray();
            await _session.SendAsync(
                PacketBuilder.PetLevelUpgrade(
                    checked((uint)pet.PetId),
                    pet.Level,
                    pet.Experience,
                    new PetSavvy(
                        values[0], values[1], values[2],
                        values[3], values[4], values[5])),
                cancellationToken,
                "DurablePetLevelUpgrade");
        }
        else if (receipt.Family ==
                 CommandFamily.PetPresenceTransition)
        {
            await _session.SendAsync(
                PacketBuilder.OwnedPetList(pets),
                cancellationToken,
                "DurablePetPresenceListRefresh");
            var target = pets.SingleOrDefault(
                candidate => candidate.PetId == receipt.PetId);
            await _session.SendAsync(
                PacketBuilder.PetOperationResult(
                    checked((uint)receipt.PetId),
                    ResolveAuthoritativePresenceResult(
                        receipt,
                        target is not null,
                        target?.IsCarried == true,
                        target?.IsSummoned == true)),
                cancellationToken,
                "DurablePetPresenceResult");
            var carried = pets.SingleOrDefault(
                static candidate => candidate.IsCarried);
            if (carried?.IsSummoned == true)
            {
                await _session.SendAsync(
                    PacketBuilder.PetWorldPresence(
                        checked((uint)carried.PetId),
                        LocalPlayerObjectId),
                    cancellationToken,
                    "DurablePetWorldPresence");
            }
            else if (carried is not null &&
                     carried.PetId != receipt.PetId)
            {
                await _session.SendAsync(
                    PacketBuilder.PetOperationResult(
                        checked((uint)carried.PetId),
                        PetOperationResultCode.TakeSucceeded),
                    cancellationToken,
                    "DurablePetCurrentTake");
            }
        }

        return true;
    }
}
