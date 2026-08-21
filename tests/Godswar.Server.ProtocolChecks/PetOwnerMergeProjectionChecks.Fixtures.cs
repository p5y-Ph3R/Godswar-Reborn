using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using System.Collections.Concurrent;

namespace Godswar.Server.ProtocolChecks;

internal sealed class OwnerMergeLifecycleTestExecutor :
    DelegatingPetDurableCommandExecutor,
    IPetOwnerMergeLifecycleStore
{
    private int _drainCount;
    private int _restoreCount;
    private readonly ConcurrentQueue<int> _restoreEnergyPointRequests = [];

    public int DrainCount => Volatile.Read(ref _drainCount);

    public int RestoreCount => Volatile.Read(ref _restoreCount);

    public int[] RestoreEnergyPointRequests =>
        _restoreEnergyPointRequests.ToArray();

    public Task<PetOwnerMergeLifecycleResult> DrainEnergyAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        int energyPoints,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _drainCount);
        return Task.FromResult(new PetOwnerMergeLifecycleResult(
            PetOwnerMergeLifecycleStatus.NoActiveMerge,
            PetId: 0,
            CurrentEnergy: 0,
            MaximumEnergy: 0,
            PetRevision: 0,
            IsCarried: false,
            IsSummoned: false));
    }

    public Task<PetOwnerMergeLifecycleResult> RestoreEnergyAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        int energyPoints,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _restoreEnergyPointRequests.Enqueue(energyPoints);
        var restoreCount = Interlocked.Increment(ref _restoreCount);
        var currentEnergy = restoreCount == 1 ? 80 : 100;
        return Task.FromResult(new PetOwnerMergeLifecycleResult(
            currentEnergy == 100
                ? PetOwnerMergeLifecycleStatus.EnergyAtMaximum
                : PetOwnerMergeLifecycleStatus.EnergyChanged,
            PetId: 1,
            CurrentEnergy: currentEnergy,
            MaximumEnergy: 100,
            PetRevision: restoreCount,
            IsCarried: true,
            IsSummoned: true));
    }

    public Task<PetOwnerMergeLifecycleResult> EndAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        PetOwnerMergeEndReason reason,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PetOwnerMergeLifecycleResult(
            PetOwnerMergeLifecycleStatus.NoActiveMerge,
            PetId: 0,
            CurrentEnergy: 0,
            MaximumEnergy: 0,
            PetRevision: 0,
            IsCarried: false,
            IsSummoned: false));
}

internal static class PetOwnerMergePacketListExtensions
{
    public static int FindIndex(
        this IReadOnlyList<byte[]> packets,
        Func<byte[], bool> predicate)
    {
        for (var index = 0; index < packets.Count; index++)
        {
            if (predicate(packets[index]))
            {
                return index;
            }
        }
        return -1;
    }
}
