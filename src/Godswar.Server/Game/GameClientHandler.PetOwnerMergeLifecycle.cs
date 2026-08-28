using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly TimeSpan _petOwnerMergeEnergyInterval;
    private CancellationTokenSource? _petOwnerMergeLifecycleCancellation;
    private Task? _petOwnerMergeLifecycleTask;
    private long _petOwnerMergeLifecycleGeneration;

    private IPetOwnerMergeLifecycleStore? PetOwnerMergeLifecycle =>
        _petDurableCommands as IPetOwnerMergeLifecycleStore;

    private void StartPetOwnerMergeEnergyDrain()
    {
        if (_registry.IsTrainingDummyCore(_character) ||
            PetOwnerMergeLifecycle is null ||
            _petOwnerMergeLifecycleTask is { IsCompleted: false })
        {
            return;
        }

        CancelPetOwnerMergeEnergyRecharge();
        CancelPetOwnerMergeEnergyDrain();
        var generation = Interlocked.Increment(
            ref _petOwnerMergeLifecycleGeneration);
        _petOwnerMergeLifecycleCancellation =
            new CancellationTokenSource();
        _petOwnerMergeLifecycleTask = RunPetOwnerMergeEnergyDrainAsync(
            generation,
            _petOwnerMergeLifecycleCancellation.Token);
    }

    private async Task RunPetOwnerMergeEnergyDrainAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            _petOwnerMergeEnergyInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    if (generation != Volatile.Read(
                            ref _petOwnerMergeLifecycleGeneration))
                    {
                        return;
                    }
                    if (!TryGetOwnerMergeLifecycleContext(
                            out var subject,
                            out var ownership) ||
                        PetOwnerMergeLifecycle is not { } lifecycle)
                    {
                        return;
                    }
                    if (_registry.IsSessionInMedusaInstance(_session))
                    {
                        continue;
                    }

                    var result = await lifecycle.DrainEnergyAsync(
                        subject,
                        ownership,
                        energyPoints: 1,
                        cancellationToken);
                    if (result.Status ==
                        PetOwnerMergeLifecycleStatus.NoActiveMerge)
                    {
                        return;
                    }
                    if (result.Status ==
                        PetOwnerMergeLifecycleStatus.EnergyChanged)
                    {
                        await _session.SendAsync(
                            PacketBuilder.PetEnergy(
                                result.CurrentEnergy,
                                result.MaximumEnergy),
                            cancellationToken,
                            "PetOwnerMergeEnergyTick");
                        continue;
                    }

                    if (!await RefreshCharacterSnapshotAsync(
                            "pet_owner_merge_expired",
                            cancellationToken) ||
                        _character is null)
                    {
                        _session.Disconnect();
                        return;
                    }
                    _registry.UpdateCharacter(
                        _session,
                        _character,
                        advanceWorldRevision: false);
                    var pet = _characterLoadSnapshot?.Pets.SingleOrDefault(
                        candidate => candidate.PetId == result.PetId);
                    if (pet is null || pet.ContributesToCharacter)
                    {
                        _session.Disconnect();
                        return;
                    }
                    await PublishPetOwnerMergeEndedAsync(
                        pet,
                        restoreCompanion: true,
                        cancellationToken);
                    return;
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[pet] owner-Merge energy settlement failed: {ex.Message}");
            _session.Disconnect();
        }
    }

    private async Task StopPetOwnerMergeEnergyDrainAsync()
    {
        Interlocked.Increment(ref _petOwnerMergeLifecycleGeneration);
        var cancellation = _petOwnerMergeLifecycleCancellation;
        var task = _petOwnerMergeLifecycleTask;
        _petOwnerMergeLifecycleCancellation = null;
        _petOwnerMergeLifecycleTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CancelPetOwnerMergeEnergyDrain()
    {
        Interlocked.Increment(ref _petOwnerMergeLifecycleGeneration);
        var cancellation = _petOwnerMergeLifecycleCancellation;
        var task = _petOwnerMergeLifecycleTask;
        _petOwnerMergeLifecycleCancellation = null;
        _petOwnerMergeLifecycleTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        _ = DisposeCancelledPetOwnerMergeTaskAsync(task, cancellation);
    }

    private static async Task DisposeCancelledPetOwnerMergeTaskAsync(
        Task? task,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[pet] cancelled owner-Merge timer failed: {ex.Message}");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task<bool> RecoverStalePetOwnerMergeOnLoginAsync(
        IReadOnlyList<PetBootstrapSnapshot> pets,
        CancellationToken cancellationToken)
    {
        if (!pets.Any(static pet => pet.ContributesToCharacter))
        {
            return false;
        }
        if (_registry.IsTrainingDummyCore(_character))
        {
            return false;
        }
        if (PetOwnerMergeLifecycle is not { } lifecycle ||
            !TryGetOwnerMergeLifecycleContext(
                out var subject,
                out var ownership))
        {
            throw new InvalidDataException(
                "An active pet owner-Merge cannot enter the world " +
                "without its durable lifecycle and ownership fence.");
        }

        var result = await lifecycle.EndAsync(
            subject,
            ownership,
            PetOwnerMergeEndReason.StaleLoginRecovery,
            cancellationToken);
        if (!result.Changed)
        {
            throw new InvalidDataException(
                "The durable pet owner-Merge recovery did not clear " +
                "the active snapshot state.");
        }

        if (!await RefreshCharacterSnapshotAsync(
                "pet_owner_merge_login_recovery",
                cancellationToken) ||
            _character is null)
        {
            throw new InvalidDataException(
                "Pet owner-Merge login recovery could not refresh state.");
        }
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _registry.SetPetOwnerMergePresentation(_session, active: false);
        Console.WriteLine(
            $"[pet] recovered stale owner Merge character={_character.Name} pet={result.PetId}");
        return true;
    }

    private async Task EndPetOwnerMergeForSessionExitAsync()
    {
        if (_registry.IsTrainingDummyCore(_character) ||
            PetOwnerMergeLifecycle is not { } lifecycle ||
            !TryGetOwnerMergeLifecycleContext(
                out var subject,
                out var ownership))
        {
            return;
        }

        var result = await lifecycle.EndAsync(
            subject,
            ownership,
            PetOwnerMergeEndReason.SessionEnded,
            CancellationToken.None);
        if (!result.Changed || _character is null || !_registered)
        {
            if (result.Changed)
            {
                _registry.SetPetOwnerMergePresentation(
                    _session,
                    active: false);
            }
            return;
        }

        _registry.SetPetOwnerMergePresentation(_session, active: false);

        // The routing session is closing, so only nearby third-pet managers
        // need the native merge-ended presentation before the player leaves.
        await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.PetOwnerMergeEnded(
                CurrentPlayerObjectId),
            CancellationToken.None,
            _session,
            "PetOwnerMergeSessionExitWorld");
    }

    private bool TryGetOwnerMergeLifecycleContext(
        out CommandSubject subject,
        out PlayerOwnershipFence ownership)
    {
        subject = default;
        ownership = default;
        if (_account is null ||
            _character is null ||
            !TryGetCharacterOwnership(_character, out ownership))
        {
            return false;
        }
        subject = new CommandSubject(_account.Id, _character.Id);
        return true;
    }
}
