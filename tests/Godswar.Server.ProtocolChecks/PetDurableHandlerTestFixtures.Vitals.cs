namespace Godswar.Server.ProtocolChecks;

internal sealed partial class PetDurableHandlerFixture
{
    private readonly PetHandlerStore _store;

    public (int AccountId, int CharacterId, int CurrentHp, int CurrentMp,
        long Revision)? SavedVitals => _store.SavedVitals;

    private sealed class PetHandlerStore : GameStoreTestStub
    {
        public (int AccountId, int CharacterId, int CurrentHp,
            int CurrentMp, long Revision)? SavedVitals { get; private set; }

        public override Task SaveCharacterVitalsAsync(
            int accountId,
            int characterId,
            int currentHp,
            int currentMp,
            long vitalsRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedVitals = (
                accountId,
                characterId,
                currentHp,
                currentMp,
                vitalsRevision);
            return Task.CompletedTask;
        }
    }
}
