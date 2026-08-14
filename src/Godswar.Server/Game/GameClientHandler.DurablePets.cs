using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IPetDurableCommandExecutor? _petDurableCommands;

    private async Task HandleDurableBagItemActivationAsync(
        PetCommandOperationIdentity identity,
        int kitBagSlot,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.BagItemActivation,
                identity,
                "provider or active character is unavailable");
            return;
        }

        var correlation = PetCorrelation(identity);
        var command = new BagItemActivationCommand(
            identity,
            kitBagSlot,
            IsSkillCastPending(MountCatalog.RideSkillId) ||
            _registry.IsRuntimeStatusActive(
                _session,
                MountCatalog.RuntimeStatusKind,
                DateTimeOffset.UtcNow)
                ? BagItemActivationExecutionConstraint.RideRuntimeBlocked
                : BagItemActivationExecutionConstraint.None);
        var unownedEnvelope = identity.IsSecureClient
            ? BagItemActivationCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        await ExecuteAndCompletePetCommandAsync(
            identity,
            CommandFamily.BagItemActivation,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task HandleDurablePetLevelUpgradeAsync(
        PetCommandOperationIdentity identity,
        long petId,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetLevelUpgrade,
                identity,
                "provider or active character is unavailable");
            return;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetLevelUpgradeCommand(identity, petId);
        var unownedEnvelope = identity.IsSecureClient
            ? PetLevelUpgradeCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetLevelUpgradeCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        await ExecuteAndCompletePetCommandAsync(
            identity,
            CommandFamily.PetLevelUpgrade,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task HandleDurablePetOwnerMergeToggleAsync(
        PetCommandOperationIdentity identity,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetOwnerMergeToggle,
                identity,
                "provider or active character is unavailable");
            return;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetOwnerMergeToggleCommand(identity);
        var unownedEnvelope = identity.IsSecureClient
            ? PetOwnerMergeToggleCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : PetOwnerMergeToggleCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        await ExecuteAndCompletePetCommandAsync(
            identity,
            CommandFamily.PetOwnerMergeToggle,
            ownership,
            () => _petDurableCommands.ExecuteAsync(
                envelope,
                cancellationToken),
            cancellationToken);
    }

    private async Task<PetDurableReceipt?> HandleDurablePetPresenceAsync(
        PetCommandOperationIdentity identity,
        long petId,
        PetPresenceOperation operation,
        CancellationToken cancellationToken)
    {
        if (!TryCreatePetSubject(identity, out var subject) ||
            _petDurableCommands is null)
        {
            RecordPetProviderUnavailable(
                CommandFamily.PetPresenceTransition,
                identity,
                "provider or active character is unavailable");
            return null;
        }

        var correlation = PetCorrelation(identity);
        var command = new PetPresenceTransitionCommand(
            identity,
            petId,
            ToPetPresenceCommandOperation(operation));
        var unownedEnvelope = identity.IsSecureClient
            ? PetPresenceTransitionCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                command)
            : identity.IsRawLocalServer
                ? PetPresenceTransitionCommandEnvelope.CreateRawLocal(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    command)
                : PetPresenceTransitionCommandEnvelope
                    .CreateServerSessionLifecycle(
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

    private async Task<PetDurableReceipt?> ExecuteAndCompletePetCommandAsync(
        PetCommandOperationIdentity identity,
        CommandFamily family,
        PlayerOwnershipFence ownership,
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
                identity.Strength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return null;
        }
        catch (Exception exception)
        {
            RecordPetProviderUnavailable(
                family,
                identity,
                exception.Message);
            return null;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return null;
        }

        if (execution.Receipt is { } receipt)
        {
            if (!await ReloadPetProjectionAsync(
                    receipt,
                    execution.Disposition,
                    cancellationToken))
            {
                RecordPetProviderUnavailable(
                    family,
                    identity,
                    "committed projection could not be reloaded");
                return null;
            }

            await SendPetLegacyResultAsync(
                identity,
                receipt,
                execution.Disposition,
                cancellationToken);
            return receipt;
        }

        if (execution.Disposition is
                PetDurableExecutionDisposition.RequestHashConflict or
                PetDurableExecutionDisposition.InvalidIntent or
                PetDurableExecutionDisposition.CharacterNotFound)
        {
            CommandMetrics.Record(
                family,
                identity.Strength,
                execution.Disposition ==
                    PetDurableExecutionDisposition.RequestHashConflict
                    ? CommandOutcome.RequestHashConflict
                    : CommandOutcome.InvalidIntent);
            if (identity.IsSecureClient)
            {
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
                        identity.OperationId),
                    cancellationToken);
            }
            return null;
        }

        RecordPetProviderUnavailable(
            family,
            identity,
            $"unresolved execution {execution.Disposition}");
        return null;
    }
}
