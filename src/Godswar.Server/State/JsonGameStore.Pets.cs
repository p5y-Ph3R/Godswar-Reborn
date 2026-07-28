namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public Task<IReadOnlyList<PetBootstrapSnapshot>> GetOwnedPetsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PetBootstrapSnapshot>>([]);
    }

    public Task<PetEggHatchResult> HatchPetEggAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            PetEggHatchResult.Rejected(
                PetEggHatchStatus.CharacterNotFound));
    }

    public Task<PetPresenceTransitionResult> TransitionPetPresenceAsync(
        int accountId,
        int characterId,
        long petId,
        PetPresenceOperation operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new PetPresenceTransitionResult(
                PetPresenceTransitionStatus.PetNotFound,
                petId,
                IsCarried: false,
                IsSummoned: false));
    }

    public Task<PetLevelUpgradeResult> UpgradePetLevelAsync(
        int accountId,
        int characterId,
        long petId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            PetLevelUpgradeResult.Rejected(
                PetLevelUpgradeStatus.PetNotFound,
                petId));
    }
}
