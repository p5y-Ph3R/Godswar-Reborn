namespace Godswar.Server.Application.Characters;

/// <summary>
/// Reads one bounded, internally consistent character bootstrap snapshot for
/// an already authenticated, server-derived account identity.
/// </summary>
internal interface ICharacterSnapshotReader
{
    Task<CharacterAccountSnapshot> ReadAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}
internal enum CharacterSlotPolicy : byte
{
    SingleCharacterV1 = 1
}

internal static class CharacterSnapshotContractVersions
{
    public const int Current = 1;
}
