using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int PetEnergyRecoveryPointsPerTick = 5;

    private readonly TimeSpan _petOwnerMergeRechargeInterval;
    private CancellationTokenSource? _petOwnerMergeRechargeCancellation;
    private Task? _petOwnerMergeRechargeTask;
    private long _petOwnerMergeRechargeGeneration;

    private void StartPetOwnerMergeEnergyRecharge()
    {
        if (_registry.IsTrainingDummyCore(_character) ||
            PetOwnerMergeLifecycle is null ||
            _petOwnerMergeRechargeTask is { IsCompleted: false })
        {
            return;
        }

        CancelPetOwnerMergeEnergyDrain();
        CancelPetOwnerMergeEnergyRecharge();
        var generation = Interlocked.Increment(
            ref _petOwnerMergeRechargeGeneration);
        _petOwnerMergeRechargeCancellation =
            new CancellationTokenSource();
        _petOwnerMergeRechargeTask = RunPetOwnerMergeEnergyRechargeAsync(
            generation,
            _petOwnerMergeRechargeCancellation.Token);
    }

    private async Task RunPetOwnerMergeEnergyRechargeAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            _petOwnerMergeRechargeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    if (generation != Volatile.Read(
                            ref _petOwnerMergeRechargeGeneration))
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

                    var result = await lifecycle.RestoreEnergyAsync(
                        subject,
                        ownership,
                        energyPoints: PetEnergyRecoveryPointsPerTick,
                        cancellationToken);
                    if (result.Status ==
                        PetOwnerMergeLifecycleStatus.NoRechargeTarget)
                    {
                        return;
                    }

                    ProjectPetOwnerMergeEnergy(result);
                    await _session.SendAsync(
                        PacketBuilder.PetEnergy(
                            result.CurrentEnergy,
                            result.MaximumEnergy),
                        cancellationToken,
                        result.Status ==
                            PetOwnerMergeLifecycleStatus.EnergyChanged
                            ? "PetOwnerMergeEnergyRecharge"
                            : "PetOwnerMergeEnergyHeartbeat");
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
                $"[pet] owner-Merge energy recovery failed: {ex.Message}");
            _session.Disconnect();
        }
    }

    private void ProjectPetOwnerMergeEnergy(
        PetOwnerMergeLifecycleResult result)
    {
        if (_characterLoadSnapshot is not { } snapshot)
        {
            return;
        }

        var found = false;
        var pets = snapshot.Pets.Select(pet =>
        {
            if (pet.PetId != result.PetId)
            {
                return pet;
            }

            found = true;
            return pet with
            {
                CurrentEnergy = result.CurrentEnergy,
                MaximumEnergy = result.MaximumEnergy,
                Revision = result.PetRevision,
                IsCarried = result.IsCarried,
                IsSummoned = result.IsSummoned
            };
        }).ToArray();
        if (found)
        {
            _characterLoadSnapshot = snapshot with { Pets = pets };
        }
    }

    private async Task StopPetOwnerMergeEnergyLifecycleAsync()
    {
        // An expiring drain may publish Merge-ended and start recharge while
        // its own task is unwinding. Drain must therefore settle first; the
        // second stop catches any recharge created during that handoff.
        await StopPetOwnerMergeEnergyDrainAsync();
        await StopPetOwnerMergeEnergyRechargeAsync();
    }

    private async Task StopPetOwnerMergeEnergyRechargeAsync()
    {
        Interlocked.Increment(ref _petOwnerMergeRechargeGeneration);
        var cancellation = _petOwnerMergeRechargeCancellation;
        var task = _petOwnerMergeRechargeTask;
        _petOwnerMergeRechargeCancellation = null;
        _petOwnerMergeRechargeTask = null;
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

    private void CancelPetOwnerMergeEnergyRecharge()
    {
        Interlocked.Increment(ref _petOwnerMergeRechargeGeneration);
        var cancellation = _petOwnerMergeRechargeCancellation;
        var task = _petOwnerMergeRechargeTask;
        _petOwnerMergeRechargeCancellation = null;
        _petOwnerMergeRechargeTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        _ = DisposeCancelledPetOwnerMergeRechargeTaskAsync(
            task,
            cancellation);
    }

    private static async Task DisposeCancelledPetOwnerMergeRechargeTaskAsync(
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
                $"[pet] cancelled owner-Merge recharge timer failed: {ex.Message}");
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
