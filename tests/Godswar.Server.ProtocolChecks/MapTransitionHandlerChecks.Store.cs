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

        public MapTransitionStore(GameCharacter character)
        {
            _character = character;
        }

        public List<PositionWrite> PositionWrites { get; } = [];

        public int PositionWriteAttempts { get; private set; }

        public int? FailPositionWriteAttempt { get; set; }

        public int EnterSyncRequests { get; private set; }

        public int SkillStateRequests { get; private set; }

        public int TalentStateRequests { get; private set; }

        private TaskCompletionSource<bool>? _npcSpawnReadStarted;

        private TaskCompletionSource<bool>? _npcSpawnReadRelease;

        public void BlockNpcSpawnReads()
        {
            _npcSpawnReadStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _npcSpawnReadRelease = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task WaitForNpcSpawnReadAsync(
            CancellationToken cancellationToken)
        {
            var started = _npcSpawnReadStarted ??
                throw new InvalidOperationException(
                    "NPC spawn reads are not blocked.");
            await started.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseNpcSpawnReads() =>
            _npcSpawnReadRelease?.TrySetResult(true);

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

        public override async Task<IReadOnlyList<NpcSpawnDefinition>>
            GetNpcSpawnDefinitionsAsync(
                short mapId,
                CancellationToken cancellationToken = default)
        {
            var started = _npcSpawnReadStarted;
            var release = _npcSpawnReadRelease;
            if (started is not null && release is not null)
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
            }

            return [];
        }

        public override Task<IReadOnlyList<CapturedMonsterSpawn>>
            GetCapturedMonsterSpawnsAsync(
                short mapId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CapturedMonsterSpawn>>([]);

        public override Task<WorldBossRespawnState?>
            GetActiveWorldBossRespawnAsync(
                short mapId,
                DateTimeOffset now,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<WorldBossRespawnState?>(null);

        public override Task<IReadOnlyList<byte[]>>
            GetEnterSyncPacketsAsync(
                CancellationToken cancellationToken = default)
        {
            EnterSyncRequests++;
            return Task.FromResult<IReadOnlyList<byte[]>>([]);
        }

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
    }
}
