using Godswar.Server.Application.Characters;

namespace Godswar.Server.Application.Pets;

/// <summary>
/// Reads one sealed pet only when the authenticated character currently owns
/// its active packed Seal Jade link.
/// </summary>
internal interface ISealedPetSnapshotReader
{
    Task<CharacterPetSnapshot?> ReadAuthorizedSealedPetAsync(
        int accountId,
        int characterId,
        long petId,
        CancellationToken cancellationToken = default);
}
