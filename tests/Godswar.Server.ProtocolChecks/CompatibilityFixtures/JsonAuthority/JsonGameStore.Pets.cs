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

}
