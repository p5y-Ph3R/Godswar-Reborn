namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public Task<IReadOnlyList<CapturedNpcSpawn>> GetCapturedNpcSpawnsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<CapturedNpcSpawn>>([]);
    }

    public Task<IReadOnlyList<NpcSpawnDefinition>> GetNpcSpawnDefinitionsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var references = NpcSpawnDefinitionFactory.FromGeneratedSeeds(mapId);
        var definitions = NpcSpawnDefinitionFactory.Create(mapId, [], [], references);
        return Task.FromResult(definitions);
    }

    public Task<IReadOnlyList<CapturedMonsterSpawn>> GetCapturedMonsterSpawnsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<CapturedMonsterSpawn>>([]);
    }

    public Task<IReadOnlyList<byte[]>> GetEnterSyncPacketsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<byte[]>>([]);
    }

}
