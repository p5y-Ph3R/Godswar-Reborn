using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private NpcSpawnDefinition[] _npcCatalogDefinitions = [];
    private long _npcCatalogRevision;

    internal MapNpcCatalogPublication PublishNpcDefinitions(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        var incoming = NpcCatalogDefinitions.CloneAndValidate(
            MapId,
            definitions);

        lock (_membershipGate)
        {
            lock (_monsterRuntimeGate)
            {
                EnsureNpcObjectIdsDoNotCollideWithPlayers(incoming);
                EnsureNpcObjectIdsDoNotCollideWithMonsters(incoming);
                var changed = !NpcCatalogDefinitions.SetEquals(
                    _npcCatalogDefinitions,
                    incoming);
                var nextRevision = changed
                    ? checked(_npcCatalogRevision + 1)
                    : _npcCatalogRevision;

                // Stage every map-owned value before crossing the ECS commit
                // boundary. After the shadow accepts the replacement, only
                // non-throwing reference/value assignments remain.
                ObserveNpcDefinitionsCore(incoming);
                if (changed)
                {
                    _npcCatalogDefinitions = incoming;
                    _npcCatalogRevision = nextRevision;
                }

                return new MapNpcCatalogPublication(
                    CreateNpcCatalogSnapshot(),
                    changed);
            }
        }
    }

    internal MapNpcCatalogSnapshot SnapshotNpcCatalog()
    {
        lock (_monsterRuntimeGate)
        {
            return CreateNpcCatalogSnapshot();
        }
    }

    internal bool IsCanonicalNpc(
        long expectedRevision,
        NpcSpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_monsterRuntimeGate)
        {
            if (expectedRevision != _npcCatalogRevision)
            {
                return false;
            }

            var canonical = Array.BinarySearch(
                _npcCatalogDefinitions,
                definition,
                NpcObjectIdComparer.Instance);
            return canonical >= 0 &&
                   NpcCatalogDefinitions.Equals(
                       _npcCatalogDefinitions[canonical],
                       definition);
        }
    }

    private void EnsureNpcObjectIdsDoNotCollideWithMonsters(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        if (_monsterRuntime is null)
        {
            return;
        }

        var monsterObjectIds = _monsterRuntime.Snapshot()
            .Select(static monster => monster.ObjectId)
            .ToHashSet();
        var collision = definitions
            .FirstOrDefault(definition =>
                monsterObjectIds.Contains(definition.ObjectId));
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"NPC object {collision.ObjectId} collides with the live monster " +
                $"runtime on map {MapId}.");
        }
    }

    private void EnsureNpcObjectIdsDoNotCollideWithPlayers(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        var playerObjectIds = _sessions.Values
            .Select(static context => context.ObjectId)
            .ToHashSet();
        var collision = definitions
            .FirstOrDefault(definition =>
                playerObjectIds.Contains(definition.ObjectId));
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"NPC object {collision.ObjectId} collides with a live player " +
                $"on map {MapId}.");
        }
    }

    private void EnsurePlayerObjectIdDoesNotCollideWithNpcs(
        GameSessionContext context)
    {
        if (_npcCatalogDefinitions.Any(
                definition => definition.ObjectId == context.ObjectId))
        {
            throw new InvalidOperationException(
                $"Player object {context.ObjectId} collides with the canonical " +
                $"NPC catalog on map {MapId}.");
        }
    }

    private void EnsureMonsterObjectIdsDoNotCollideWithNpcs(
        IReadOnlyList<CapturedMonsterSpawn> definitions)
    {
        var npcObjectIds = _npcCatalogDefinitions
            .Select(static definition => definition.ObjectId)
            .ToHashSet();
        var collision = definitions
            .FirstOrDefault(definition =>
                npcObjectIds.Contains(definition.ObjectId));
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"Monster object {collision.ObjectId} collides with the canonical NPC " +
                $"catalog on map {MapId}.");
        }
    }

    private MapNpcCatalogSnapshot CreateNpcCatalogSnapshot() =>
        new(
            MapId,
            _npcCatalogRevision,
            NpcCatalogDefinitions.ReadOnlyClone(_npcCatalogDefinitions));

    private sealed class NpcObjectIdComparer :
        IComparer<NpcSpawnDefinition>
    {
        public static NpcObjectIdComparer Instance { get; } = new();

        public int Compare(
            NpcSpawnDefinition? left,
            NpcSpawnDefinition? right) =>
            (left?.ObjectId ?? 0).CompareTo(right?.ObjectId ?? 0);
    }
}
