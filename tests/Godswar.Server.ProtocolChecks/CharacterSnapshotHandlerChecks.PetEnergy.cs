using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterSnapshotHandlerChecks
{
    private sealed class SnapshotLoginPetEnergyLifecycle(
        PetBootstrapSnapshot pet) :
        DelegatingPetDurableCommandExecutor,
        IPetOwnerMergeLifecycleStore
    {
        public int RestoreCount { get; private set; }
        public int CurrentEnergy { get; private set; } = pet.CurrentEnergy;
        public int MaximumEnergy => pet.MaximumEnergy;

        public Task<PetOwnerMergeLifecycleResult> DrainEnergyAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            int energyPoints,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PetOwnerMergeLifecycleResult> RestoreEnergyAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            int energyPoints,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(
                int.MaxValue,
                energyPoints,
                "snapshot login requests a complete pet-energy refill");
            RestoreCount++;
            var changed = CurrentEnergy != MaximumEnergy;
            CurrentEnergy = MaximumEnergy;
            return Task.FromResult(new PetOwnerMergeLifecycleResult(
                changed
                    ? PetOwnerMergeLifecycleStatus.EnergyChanged
                    : PetOwnerMergeLifecycleStatus.EnergyAtMaximum,
                pet.PetId,
                CurrentEnergy,
                MaximumEnergy,
                pet.Revision + (changed ? 1 : 0),
                IsCarried: true,
                pet.IsSummoned));
        }

        public Task<PetOwnerMergeLifecycleResult> EndAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            PetOwnerMergeEndReason reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
