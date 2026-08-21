namespace Godswar.Server.Application.Characters;

using Godswar.Server.Domain.World.Instances;

/// <summary>
/// Reads one bounded, internally consistent character bootstrap snapshot for
/// an already authenticated, server-derived account identity.
/// </summary>
internal interface ICharacterSnapshotReader
{
    Task<CharacterAccountSnapshot> ReadAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task<CharacterAccountSnapshot> ReadAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default) =>
        realmId == RealmId.Tempest
            ? ReadAsync(accountId, cancellationToken)
            : throw new NotSupportedException(
                "This character snapshot provider is Tempest-only.");
}
internal enum CharacterSlotPolicy : byte
{
    SingleCharacterV1 = 1
}

internal static class CharacterSnapshotContractVersions
{
    public const int Current = 1;
}
