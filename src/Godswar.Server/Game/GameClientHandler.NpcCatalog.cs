using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly Dictionary<uint, NpcSpawnDefinition>
        _mapNpcsByObjectId = [];
    private WorldInstanceId _npcCatalogWorldInstanceId;
    private long _npcCatalogRevision;
    private NpcCatalogSubscription? _npcCatalogSubscription;

    private void StartNpcCatalogUpdates()
    {
        _npcCatalogSubscription ??=
            _registry.RegisterNpcCatalogUpdates(
                _session,
                ApplyNpcCatalogRevisionAsync);
    }

    private async Task StopNpcCatalogUpdatesAsync()
    {
        var subscription = Interlocked.Exchange(
            ref _npcCatalogSubscription,
            null);
        if (subscription is not null)
        {
            await _registry.UnregisterNpcCatalogUpdatesAsync(subscription);
        }
    }

    private void InstallNpcCatalog(MapNpcCatalogSnapshot snapshot)
    {
        if (_character is null ||
            snapshot.MapId != _character.CurrentMap)
        {
            throw new InvalidOperationException(
                $"NPC catalog map {snapshot.MapId} does not match the " +
                "active character map.");
        }

        var tracker = CreateNpcVisibilityTracker(snapshot.Definitions);
        ReplaceLocalNpcCatalog(snapshot, tracker);
    }

    private async Task ApplyNpcCatalogRevisionAsync(
        MapNpcCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await _characterStateGate.WaitAsync(cancellationToken);
        try
        {
            if (_character is null ||
                snapshot.MapId != _character.CurrentMap ||
                snapshot.WorldInstanceId !=
                    _npcCatalogWorldInstanceId ||
                !_registry.IsSessionInWorldInstance(
                    _session,
                    snapshot.WorldInstanceId) ||
                snapshot.Revision <= _npcCatalogRevision ||
                !_registry.IsCanonicalMapNpcCatalog(
                    snapshot.MapId,
                    snapshot.Revision,
                    _session))
            {
                return;
            }

            var replacementTracker =
                CreateNpcVisibilityTracker(snapshot.Definitions);
            if (!replacementTracker.TryCalculate(
                    _character.PositionX,
                    _character.PositionZ,
                    out var replacementDelta))
            {
                throw new InvalidOperationException(
                    $"Character {_character.Id} has invalid coordinates for " +
                    $"NPC catalog revision {snapshot.Revision}.");
            }

            var oldVisible = _npcVisibility?
                .SnapshotVisibleObjectIds()
                .ToHashSet() ?? [];
            var desired = replacementDelta.Entering
                .OrderBy(static npc => npc.ObjectId)
                .ToArray();
            var desiredObjectIds = desired
                .Select(static npc => npc.ObjectId)
                .ToHashSet();
            var replacements = desired
                .Where(npc =>
                    oldVisible.Contains(npc.ObjectId) &&
                    (!_mapNpcsByObjectId.TryGetValue(
                         npc.ObjectId,
                         out var previous) ||
                     !NpcCatalogDefinitions.Equals(previous, npc)))
                .Select(static npc => npc.ObjectId)
                .ToHashSet();
            var leaving = oldVisible
                .Where(objectId =>
                    !desiredObjectIds.Contains(objectId) ||
                    replacements.Contains(objectId))
                .OrderBy(static objectId => objectId)
                .ToArray();
            var entering = desired
                .Where(npc =>
                    !oldVisible.Contains(npc.ObjectId) ||
                    replacements.Contains(npc.ObjectId))
                .ToArray();

            if (leaving.Length > 0)
            {
                await _session.SendAsync(
                    PacketBuilder.RemoveWorldObjects(leaving),
                    cancellationToken,
                    "NpcCatalogRevisionRemovals");
            }

            if (entering.Length > 0)
            {
                await _session.SendAsync(
                    PacketBuilder.NpcSpawns(entering),
                    cancellationToken,
                    "NpcCatalogRevisionSpawns",
                    framed: false);
            }

            replacementTracker.Commit(replacementDelta);
            ReplaceLocalNpcCatalog(snapshot, replacementTracker);
            ClearGearEnhancerSelection();
            Console.WriteLine(
                $"[npc] applied catalog revision character={_character.Name} " +
                $"map={snapshot.MapId} revision={snapshot.Revision} " +
                $"removed={leaving.Length} spawned={entering.Length}");
        }
        finally
        {
            _characterStateGate.Release();
        }
    }

    private void ReplaceLocalNpcCatalog(
        MapNpcCatalogSnapshot snapshot,
        WorldSectorVisibilityTracker<NpcSpawnDefinition> tracker)
    {
        _mapNpcsByInteractionId.Clear();
        _mapNpcsByObjectId.Clear();
        foreach (var npc in snapshot.Definitions)
        {
            _mapNpcsByInteractionId.Add(npc.InteractionId, npc);
            _mapNpcsByObjectId.Add(npc.ObjectId, npc);
        }

        _npcVisibility = tracker;
        _npcCatalogWorldInstanceId =
            snapshot.WorldInstanceId;
        _npcCatalogRevision = snapshot.Revision;
    }

    private void ClearLocalNpcCatalog()
    {
        _npcVisibility = null;
        _mapNpcsByInteractionId.Clear();
        _mapNpcsByObjectId.Clear();
        _npcCatalogWorldInstanceId = default;
        _npcCatalogRevision = 0;
    }

    private static WorldSectorVisibilityTracker<NpcSpawnDefinition>
        CreateNpcVisibilityTracker(
            IEnumerable<NpcSpawnDefinition> definitions) =>
        new(
            definitions,
            static npc => npc.ObjectId,
            static npc => npc.X,
            static npc => npc.Z,
            "NPC");
}
