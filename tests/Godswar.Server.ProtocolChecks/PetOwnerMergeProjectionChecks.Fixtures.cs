using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal sealed class OwnerMergeLifecycleTestExecutor :
    DelegatingPetDurableCommandExecutor,
    IPetOwnerMergeLifecycleStore
{
    private int _drainCount;

    public int DrainCount => Volatile.Read(ref _drainCount);

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
