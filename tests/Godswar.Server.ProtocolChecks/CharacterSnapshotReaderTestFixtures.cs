using Godswar.Server.Application.Characters;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterSnapshotReaderTestFixtures
{
    public static ICharacterSnapshotReader Empty { get; } =
        new EmptyCharacterSnapshotReader();

    public static ICharacterSnapshotReader Unused { get; } =
        new UnusedCharacterSnapshotReader();

    private sealed class EmptyCharacterSnapshotReader :
        ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new CharacterAccountSnapshot(
                CharacterSnapshotContractVersions.Current,
                accountId,
                $"protocol-check-empty-{accountId}",
                DateTimeOffset.UtcNow,
                CharacterSlotPolicy.SingleCharacterV1,
                Character: null);
            CharacterSnapshotContract.Validate(snapshot);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class UnusedCharacterSnapshotReader :
        ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.InvalidData,
                "This protocol check did not expect a character snapshot read.");
        }
    }
}
