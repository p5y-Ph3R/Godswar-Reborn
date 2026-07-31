using System.Collections.Immutable;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.Application.Pets;

/// <summary>
/// Reads one bounded, internally consistent projection of the pets owned by
/// an active character.
/// </summary>
internal interface IOwnedPetSnapshotReader
{
    Task<ImmutableArray<CharacterPetSnapshot>> ReadOwnedPetsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);
}
