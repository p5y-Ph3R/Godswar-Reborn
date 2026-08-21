using Godswar.Server.Application.Characters;

namespace Godswar.Server.ProtocolChecks;

internal sealed partial class PetDurableHandlerFixture
{
    private sealed class FixedSnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "pet snapshot account");
            return Task.FromResult(snapshot);
        }
    }
}
