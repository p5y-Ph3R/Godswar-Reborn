using Godswar.Server.Application.World;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MapTransitionHandlerChecks
{
    private readonly record struct PositionWrite(
        int AccountId,
        int CharacterId,
        byte MapId,
        float X,
        float Z);

    private sealed class MapTransitionStore : GameStoreTestStub
    {
        private readonly GameCharacter _character;
        private readonly IReadOnlyList<PetBootstrapSnapshot> _pets;
        private readonly MapTransitionWorldContentReader _worldContent =
            new();

        public MapTransitionStore(
            GameCharacter character,
            IReadOnlyList<PetBootstrapSnapshot>? pets = null)
        {
            _character = character;
            _pets = pets ?? [];
        }

        public List<PositionWrite> PositionWrites { get; } = [];

        public int PositionWriteAttempts { get; private set; }

        public int? FailPositionWriteAttempt { get; set; }

        public int EnterSyncRequests =>
            _worldContent.EnterSyncRequests;

        public int SkillStateRequests { get; private set; }

        public int TalentStateRequests { get; private set; }

        public int PetPresenceReads { get; private set; }

        public IWorldContentReader WorldContent =>
            _worldContent;

        public void BlockNpcSpawnReads() =>
            _worldContent.BlockMapReads();

        public Task WaitForNpcSpawnReadAsync(
            CancellationToken cancellationToken) =>
            _worldContent.WaitForMapReadAsync(cancellationToken);

        public void ReleaseNpcSpawnReads() =>
            _worldContent.ReleaseMapReads();

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            PositionWriteAttempts++;
            if (PositionWriteAttempts == FailPositionWriteAttempt)
            {
                throw new InvalidOperationException(
                    "Injected map-position compensation failure.");
            }

            PositionWrites.Add(new PositionWrite(
                accountId,
                characterId,
                currentMap,
                positionX,
                positionZ));
            return Task.CompletedTask;
        }

        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterStats?>(
                CharacterStats.FromCharacter(_character));

        public override Task<WorldBossRespawnState?>
            GetActiveWorldBossRespawnAsync(
                short mapId,
                DateTimeOffset now,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<WorldBossRespawnState?>(null);

        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default)
        {
            SkillStateRequests++;
            return Task.FromResult<IReadOnlyList<SkillState>>([]);
        }

        public override Task<IReadOnlyList<TalentState>>
            GetTalentStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default)
        {
            TalentStateRequests++;
            return Task.FromResult<IReadOnlyList<TalentState>>([]);
        }

        public override Task<IReadOnlyList<PetBootstrapSnapshot>>
            GetOwnedPetsAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default)
        {
            PetPresenceReads++;
            return Task.FromResult(_pets);
        }
    }

    private sealed class MapTransitionWorldContentReader :
        IWorldContentReader
    {
        private readonly IWorldContentReader _inner =
            WorldContentReaderTestFixtures.Empty;
        private TaskCompletionSource<bool>? _mapReadStarted;
        private TaskCompletionSource<bool>? _mapReadRelease;

        public WorldContentManifest Manifest =>
            _inner.Manifest;

        public int EnterSyncRequests { get; private set; }

        public void BlockMapReads()
        {
            _mapReadStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _mapReadRelease = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task WaitForMapReadAsync(
            CancellationToken cancellationToken)
        {
            var started = _mapReadStarted ??
                throw new InvalidOperationException(
                    "Map-content reads are not blocked.");
            await started.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseMapReads() =>
            _mapReadRelease?.TrySetResult(true);

        public async ValueTask<WorldMapContent> ReadMapAsync(
            short mapId,
            CancellationToken cancellationToken = default)
        {
            var started = _mapReadStarted;
            var release = _mapReadRelease;
            if (started is not null && release is not null)
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
            }

            return await _inner.ReadMapAsync(
                mapId,
                cancellationToken);
        }

        public ValueTask<EnterWorldBootstrapContent>
            ReadEnterBootstrapAsync(
                CancellationToken cancellationToken = default)
        {
            EnterSyncRequests++;
            return _inner.ReadEnterBootstrapAsync(cancellationToken);
        }

        public ValueTask<NpcDialogueContent> ReadNpcDialogueAsync(
            string npcKey,
            CancellationToken cancellationToken = default) =>
            _inner.ReadNpcDialogueAsync(npcKey, cancellationToken);
    }
}
