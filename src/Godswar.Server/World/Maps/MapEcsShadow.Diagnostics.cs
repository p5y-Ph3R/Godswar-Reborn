using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Npcs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Maps;

internal sealed partial class MapEcsShadow
{
    public int PlayerCount
    {
        get
        {
            lock (_gate)
            {
                return _players.Count;
            }
        }
    }

    public bool ContainsPlayer(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            return _players.TryGetValue(session, out var binding) &&
                   _world.IsAlive(binding.Entity);
        }
    }

    public IReadOnlyList<ClientSession> SnapshotPlayerSessions()
    {
        lock (_gate)
        {
            return _players
                .Where(pair => _world.IsAlive(pair.Value.Entity))
                .OrderBy(static pair => pair.Value.ObjectId)
                .Select(static pair => pair.Key)
                .ToArray();
        }
    }

    public IReadOnlyList<NpcSpawnDefinition> SnapshotNpcDefinitions()
    {
        lock (_gate)
        {
            return _npcsByObjectId
                .OrderBy(static pair => pair.Key)
                .Select(pair => NpcEcsSnapshotAdapter.ToSpawnDefinition(
                    NpcEcsSnapshotAdapter.Capture(_world, pair.Value)))
                .ToArray();
        }
    }

    public MapEcsShadowSnapshot Snapshot()
    {
        lock (_gate)
        {
            var players = _players.Values
                .OrderBy(static binding => binding.ObjectId)
                .Select(binding => CapturePlayer(binding.Entity))
                .ToArray();
            var npcs = _npcsByObjectId
                .OrderBy(static pair => pair.Key)
                .Select(pair => new MapEcsNpcSnapshot(
                    pair.Value,
                    NpcEcsSnapshotAdapter.Capture(_world, pair.Value)))
                .ToArray();
            return new MapEcsShadowSnapshot(
                MapId,
                _revision,
                players,
                npcs,
                ActiveFaults(),
                _faultCount);
        }
    }

    public MapEcsParityDiagnostics Diagnose(
        IReadOnlyList<GameSessionContext> livePlayers)
    {
        ArgumentNullException.ThrowIfNull(livePlayers);

        lock (_gate)
        {
            var liveSessions = livePlayers
                .Select(static context => context.Session)
                .ToHashSet();
            var missing = new List<uint>();
            var mismatched = new List<uint>();
            foreach (var context in livePlayers)
            {
                if (!_players.TryGetValue(context.Session, out var binding))
                {
                    missing.Add(context.ObjectId);
                    continue;
                }

                if (binding.ObjectId != context.ObjectId ||
                    !_world.IsAlive(binding.Entity) ||
                    !PlayerMatches(
                        context,
                        binding.Entity))
                {
                    mismatched.Add(context.ObjectId);
                }
            }

            var unexpected = _players
                .Where(pair => !liveSessions.Contains(pair.Key))
                .Select(static pair => pair.Value.ObjectId)
                .Order()
                .ToArray();
            var npcMismatches = DiagnoseNpcs();
            return new MapEcsParityDiagnostics(
                MapId,
                _revision,
                livePlayers.Count,
                _players.Count,
                _authoritativeNpcs.Count,
                _npcsByObjectId.Count,
                missing.Order().ToArray(),
                unexpected,
                mismatched.Order().ToArray(),
                npcMismatches,
                ActiveFaults(),
                _faultCount);
        }
    }

    public bool TryGetPlayerEntity(
        ClientSession session,
        out EntityId entity)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (_players.TryGetValue(session, out var binding) &&
                _world.IsAlive(binding.Entity))
            {
                entity = binding.Entity;
                return true;
            }

            entity = EntityId.None;
            return false;
        }
    }

    public bool TryGetNpcEntity(
        uint objectId,
        out EntityId entity)
    {
        lock (_gate)
        {
            if (_npcsByObjectId.TryGetValue(objectId, out entity) &&
                _world.IsAlive(entity))
            {
                return true;
            }

            entity = EntityId.None;
            return false;
        }
    }

    public bool TryGetEntityByObjectId(
        uint objectId,
        out EntityId entity)
    {
        lock (_gate)
        {
            if (_entitiesByObjectId.TryGetValue(objectId, out entity) &&
                _world.IsAlive(entity))
            {
                return true;
            }

            entity = EntityId.None;
            return false;
        }
    }

    public bool IsEntityAlive(EntityId entity)
    {
        lock (_gate)
        {
            return _world.IsAlive(entity);
        }
    }

    private MapEcsPlayerSnapshot CapturePlayer(EntityId entity)
    {
        var presence = _world.Get<MapPlayerPresenceComponent>(entity);
        return new MapEcsPlayerSnapshot(
            entity,
            presence.WorldReady,
            PlayerEcsSnapshotAdapter.Capture(_world, entity));
    }

    private bool PlayerMatches(
        GameSessionContext context,
        EntityId actualEntity)
    {
        var expectedWorld = new EcsWorld();
        var expectedEntity = GameCharacterEcsHydrator.Hydrate(
            expectedWorld,
            context.Character,
            context.ObjectId,
            context.WorldRevision,
            NeutralPlayerStatus);
        var expected =
            PlayerEcsSnapshotAdapter.Capture(expectedWorld, expectedEntity);
        var actual =
            PlayerEcsSnapshotAdapter.Capture(_world, actualEntity);
        var presence =
            _world.Get<MapPlayerPresenceComponent>(actualEntity);
        return presence.WorldReady == context.WorldReady &&
               PlayerSnapshotsEqual(expected, actual);
    }

    private uint[] DiagnoseNpcs()
    {
        var mismatches = new List<uint>();
        foreach (var source in _authoritativeNpcs)
        {
            if (!_npcsByObjectId.TryGetValue(
                    source.Key,
                    out var entity) ||
                !_world.IsAlive(entity))
            {
                mismatches.Add(source.Key);
                continue;
            }

            var actual = NpcEcsSnapshotAdapter.ToSpawnDefinition(
                NpcEcsSnapshotAdapter.Capture(_world, entity));
            if (!NpcDefinitionsEqual(source.Value, actual))
            {
                mismatches.Add(source.Key);
            }
        }

        foreach (var objectId in _npcsByObjectId.Keys)
        {
            if (!_authoritativeNpcs.ContainsKey(objectId))
            {
                mismatches.Add(objectId);
            }
        }

        return mismatches.Distinct().Order().ToArray();
    }

    private string[] ActiveFaults()
    {
        return _playerFaults.Values
            .Concat(_npcFault is null ? [] : new[] { _npcFault })
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool PlayerSnapshotsEqual(
        PlayerEcsSnapshot expected,
        PlayerEcsSnapshot actual)
    {
        return expected.Identity == actual.Identity &&
               expected.Class == actual.Class &&
               expected.Camp == actual.Camp &&
               expected.Transform == actual.Transform &&
               expected.Vitals.Equals(actual.Vitals) &&
               expected.Progression == actual.Progression &&
               expected.Wallet == actual.Wallet &&
               expected.EquipmentAppearance ==
               actual.EquipmentAppearance &&
               expected.Zodiac == actual.Zodiac &&
               expected.CalculatedStats == actual.CalculatedStats &&
               expected.StatusEffects.SequenceEqual(actual.StatusEffects) &&
               expected.StatusAggregate == actual.StatusAggregate &&
               string.Equals(
                   expected.StatusFingerprint,
                   actual.StatusFingerprint,
                   StringComparison.Ordinal);
    }

    private static NpcSpawnDefinition CloneNpcDefinition(
        NpcSpawnDefinition definition) =>
        definition with
        {
            Detail10077 = definition.Detail10077.ToArray(),
            Detail10080 = definition.Detail10080.ToArray()
        };

    private static bool NpcDefinitionsEqual(
        NpcSpawnDefinition left,
        NpcSpawnDefinition right)
    {
        return left.MapId == right.MapId &&
               string.Equals(
                   left.SceneKey,
                   right.SceneKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   left.NpcKey,
                   right.NpcKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   left.TemplateKey,
                   right.TemplateKey,
                   StringComparison.Ordinal) &&
               left.ObjectId == right.ObjectId &&
               left.X.Equals(right.X) &&
               left.Z.Equals(right.Z) &&
               left.InteractionId == right.InteractionId &&
               left.AppearanceType == right.AppearanceType &&
               left.Facing.Equals(right.Facing) &&
               left.Detail10077.SequenceEqual(right.Detail10077) &&
               left.Detail10080.SequenceEqual(right.Detail10080);
    }
}
