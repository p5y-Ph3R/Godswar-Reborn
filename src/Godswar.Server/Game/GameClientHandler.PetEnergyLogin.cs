using Godswar.Server.Application.Pets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task RefillCarriedPetEnergyForLoginAsync(
        CancellationToken cancellationToken)
    {
        // Pinned player-backed dummies own a perpetual Merge fixture. Their
        // energy and contribution state must remain untouched by user-login
        // normalization.
        if (_registry.IsTrainingDummyCore(_character))
        {
            return;
        }

        var snapshot = _characterLoadSnapshot ??
            throw new InvalidDataException(
                "Pet energy cannot be normalized without a loaded snapshot.");
        var carriedPets = snapshot.Pets
            .Where(static pet => pet.IsCarried)
            .Take(2)
            .ToArray();
        if (carriedPets.Length == 0)
        {
            return;
        }
        if (carriedPets.Length != 1)
        {
            throw new InvalidDataException(
                "A character cannot enter with multiple carried pets.");
        }

        var carried = carriedPets[0];
        if (carried.ContributesToCharacter)
        {
            throw new InvalidDataException(
                "Stale owner-Merge recovery must finish before the login " +
                "energy refill.");
        }
        if (PetOwnerMergeLifecycle is not { } lifecycle)
        {
            throw new InvalidDataException(
                "The carried pet cannot be refilled without a durable " +
                "pet lifecycle.");
        }
        if (!TryGetOwnerMergeLifecycleContext(
                out var subject,
                out var ownership))
        {
            throw new InvalidDataException(
                "The carried pet cannot be refilled without current " +
                "ownership authority.");
        }

        // RestoreEnergyAsync locks the one carried row, validates the player
        // ownership fence, and compare-and-sets its pet revision. The maximal
        // bounded increment expresses a full refill while retaining the same
        // transaction used by ordinary online recharge.
        var result = await lifecycle.RestoreEnergyAsync(
            subject,
            ownership,
            energyPoints: int.MaxValue,
            cancellationToken);
        result.Validate();
        if (result.Status is not
                (PetOwnerMergeLifecycleStatus.EnergyChanged or
                 PetOwnerMergeLifecycleStatus.EnergyAtMaximum) ||
            result.PetId != carried.PetId ||
            !result.IsCarried ||
            result.MaximumEnergy <= 0 ||
            result.CurrentEnergy != result.MaximumEnergy)
        {
            throw new InvalidDataException(
                "The durable carried-pet login refill was incomplete.");
        }

        ProjectPetOwnerMergeEnergy(result);
        var projected = _characterLoadSnapshot?.Pets.SingleOrDefault(
            pet => pet.PetId == result.PetId);
        if (projected is null ||
            !projected.IsCarried ||
            projected.CurrentEnergy != projected.MaximumEnergy ||
            projected.Revision != result.PetRevision)
        {
            throw new InvalidDataException(
                "The carried-pet login refill was not projected.");
        }

        Console.WriteLine(
            $"[pet] login energy normalized character={_character?.Name} " +
            $"pet={result.PetId} energy={result.CurrentEnergy}/" +
            $"{result.MaximumEnergy}");
    }
}
