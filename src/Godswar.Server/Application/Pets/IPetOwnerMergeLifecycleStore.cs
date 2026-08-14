using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal enum PetOwnerMergeEndReason : byte
{
    EnergyDepleted = 1,
    SessionEnded = 2,
    StaleLoginRecovery = 3
}

internal enum PetOwnerMergeLifecycleStatus : byte
{
    NoActiveMerge = 1,
    EnergyChanged = 2,
    MergeEnded = 3
}

internal sealed record PetOwnerMergeLifecycleResult(
    PetOwnerMergeLifecycleStatus Status,
    long PetId,
    int CurrentEnergy,
    int MaximumEnergy,
    long PetRevision,
    bool IsCarried,
    bool IsSummoned)
{
    public bool Changed => Status !=
        PetOwnerMergeLifecycleStatus.NoActiveMerge;

    public void Validate()
    {
        if (!Enum.IsDefined(Status) ||
            PetId < 0 ||
            CurrentEnergy < 0 ||
            MaximumEnergy < 0 ||
            CurrentEnergy > MaximumEnergy ||
            PetRevision < 0 ||
            (Status == PetOwnerMergeLifecycleStatus.NoActiveMerge) !=
                (PetId == 0) ||
            (Status == PetOwnerMergeLifecycleStatus.NoActiveMerge) !=
                (MaximumEnergy == 0) ||
            Status == PetOwnerMergeLifecycleStatus.EnergyChanged &&
                CurrentEnergy == 0)
        {
            throw new InvalidDataException(
                "Pet owner-Merge lifecycle result is inconsistent.");
        }
    }
}

internal interface IPetOwnerMergeLifecycleStore
{
    Task<PetOwnerMergeLifecycleResult> DrainEnergyAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        int energyPoints,
        CancellationToken cancellationToken = default);

    Task<PetOwnerMergeLifecycleResult> EndAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        PetOwnerMergeEndReason reason,
        CancellationToken cancellationToken = default);
}
