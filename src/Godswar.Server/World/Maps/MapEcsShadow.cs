using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Npcs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Maps;

/// <summary>
/// Generation-safe player and NPC entity store for one live map. In ECS mode
/// this owns gameplay membership and canonical NPC snapshots; the session
/// dictionary remains only a transport lookup. Legacy mode retains this store
/// as a parity shadow. Copy/swap writes keep readers from observing a partially
/// replaced entity.
/// </summary>
internal sealed partial class MapEcsShadow
{
    private const string NeutralStatusFingerprint = "map-ecs-shadow-neutral";

    private static readonly PlayerStatusSnapshot NeutralPlayerStatus = new(
        [],
        ClientStatusAggregate.Empty,
        NeutralStatusFingerprint);

    private readonly object _gate = new();
    private readonly EcsWorld _world = new();
    private Dictionary<ClientSession, PlayerBinding> _players = [];
    private Dictionary<uint, EntityId> _entitiesByObjectId = [];
    private Dictionary<uint, EntityId> _npcsByObjectId = [];
    private Dictionary<uint, NpcSpawnDefinition> _authoritativeNpcs = [];
    private readonly Dictionary<ClientSession, string> _playerFaults = [];
    private string? _npcFault;
    private long _revision;
    private long _faultCount;

    public MapEcsShadow(byte mapId)
    {
        MapId = mapId;
        GameCharacterEcsHydrator.RegisterComponents(_world);
        NpcSpawnDefinitionEcsHydrator.RegisterComponents(_world);
        _world.RegisterComponent<MapPlayerPresenceComponent>();
    }

    public byte MapId { get; }

    public bool TryAddOrUpdatePlayer(
        GameSessionContext context,
        PlayerTransformOverride? transformOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            var stagedEntity = EntityId.None;
            try
            {
                ValidatePlayerContext(context, transformOverride);
                stagedEntity = GameCharacterEcsHydrator.Hydrate(
                    _world,
                    context.Character,
                    context.ObjectId,
                    context.WorldRevision,
                    NeutralPlayerStatus,
                    transformOverride);
                _world.Add(
                    stagedEntity,
                    new MapPlayerPresenceComponent(context.WorldReady));

                var hasPrevious = _players.TryGetValue(
                    context.Session,
                    out var previous);
                if (_entitiesByObjectId.TryGetValue(
                        context.ObjectId,
                        out var occupied) &&
                    (!hasPrevious || occupied != previous.Entity))
                {
                    throw new InvalidOperationException(
                        $"World object {context.ObjectId} is already mirrored.");
                }

                var nextPlayers =
                    new Dictionary<ClientSession, PlayerBinding>(_players)
                    {
                        [context.Session] = new PlayerBinding(
                            stagedEntity,
                            context.ObjectId)
                    };
                var nextObjects =
                    new Dictionary<uint, EntityId>(_entitiesByObjectId)
                    {
                        [context.ObjectId] = stagedEntity
                    };
                if (hasPrevious &&
                    previous.ObjectId != context.ObjectId &&
                    nextObjects.TryGetValue(
                        previous.ObjectId,
                        out var previousObjectEntity) &&
                    previousObjectEntity == previous.Entity)
                {
                    nextObjects.Remove(previous.ObjectId);
                }

                _players = nextPlayers;
                _entitiesByObjectId = nextObjects;
                if (hasPrevious)
                {
                    _world.TryDestroyEntity(previous.Entity);
                }

                _playerFaults.Remove(context.Session);
                _revision++;
                return true;
            }
            catch (Exception ex)
            {
                if (stagedEntity.IsValid)
                {
                    _world.TryDestroyEntity(stagedEntity);
                }

                RecordPlayerFault(context, ex);
                return false;
            }
        }
    }

    public bool TryRemovePlayer(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            var clearedFault = _playerFaults.Remove(session);
            if (!_players.TryGetValue(session, out var binding))
            {
                if (clearedFault)
                {
                    _revision++;
                }

                return false;
            }

            var nextPlayers =
                new Dictionary<ClientSession, PlayerBinding>(_players);
            nextPlayers.Remove(session);
            var nextObjects =
                new Dictionary<uint, EntityId>(_entitiesByObjectId);
            if (nextObjects.TryGetValue(
                    binding.ObjectId,
                    out var mappedEntity) &&
                mappedEntity == binding.Entity)
            {
                nextObjects.Remove(binding.ObjectId);
            }

            _players = nextPlayers;
            _entitiesByObjectId = nextObjects;
            _world.TryDestroyEntity(binding.Entity);
            _revision++;
            return true;
        }
    }

    public void ClearPlayerFault(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (_playerFaults.Remove(session))
            {
                _revision++;
            }
        }
    }

    public bool TryObserveNpcDefinitions(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        lock (_gate)
        {
            var stagedEntities = new List<EntityId>();
            try
            {
                var incoming = CloneAndValidateNpcDefinitions(definitions);
                if (NpcSetMatchesCurrent(incoming))
                {
                    if (_npcFault is not null)
                    {
                        _npcFault = null;
                        _revision++;
                    }

                    return true;
                }

                var nextNpcs = new Dictionary<uint, EntityId>(incoming.Count);
                foreach (var pair in incoming.OrderBy(static pair => pair.Key))
                {
                    if (_authoritativeNpcs.TryGetValue(
                            pair.Key,
                            out var existingDefinition) &&
                        NpcDefinitionsEqual(existingDefinition, pair.Value) &&
                        _npcsByObjectId.TryGetValue(
                            pair.Key,
                            out var existingEntity) &&
                        _world.IsAlive(existingEntity))
                    {
                        nextNpcs[pair.Key] = existingEntity;
                        continue;
                    }

                    if (_entitiesByObjectId.TryGetValue(
                            pair.Key,
                            out var occupied) &&
                        (!_npcsByObjectId.TryGetValue(
                             pair.Key,
                             out var previousNpcEntity) ||
                         occupied != previousNpcEntity))
                    {
                        throw new InvalidOperationException(
                            $"NPC object {pair.Key} collides with another map entity.");
                    }

                    var entity = NpcSpawnDefinitionEcsHydrator.Hydrate(
                        _world,
                        pair.Value);
                    stagedEntities.Add(entity);
                    nextNpcs[pair.Key] = entity;
                }

                CommitNpcReplacement(incoming, nextNpcs);
                _npcFault = null;
                _revision++;
                return true;
            }
            catch (Exception ex)
            {
                foreach (var entity in stagedEntities)
                {
                    _world.TryDestroyEntity(entity);
                }

                _npcFault =
                    $"npc map={MapId}: {ex.GetType().Name}: {ex.Message}";
                _faultCount++;
                _revision++;
                return false;
            }
        }
    }

    private void CommitNpcReplacement(
        Dictionary<uint, NpcSpawnDefinition> incoming,
        Dictionary<uint, EntityId> nextNpcs)
    {
        var retainedEntities = nextNpcs.Values.ToHashSet();
        var nextObjects = new Dictionary<uint, EntityId>(_entitiesByObjectId);
        foreach (var previous in _npcsByObjectId)
        {
            if (nextObjects.TryGetValue(
                    previous.Key,
                    out var mappedEntity) &&
                mappedEntity == previous.Value)
            {
                nextObjects.Remove(previous.Key);
            }
        }

        foreach (var current in nextNpcs)
        {
            nextObjects[current.Key] = current.Value;
        }

        var previousEntities = _npcsByObjectId.Values.ToArray();
        _entitiesByObjectId = nextObjects;
        _npcsByObjectId = nextNpcs;
        _authoritativeNpcs = incoming;
        foreach (var previousEntity in previousEntities)
        {
            if (!retainedEntities.Contains(previousEntity))
            {
                _world.TryDestroyEntity(previousEntity);
            }
        }
    }

    private Dictionary<uint, NpcSpawnDefinition> CloneAndValidateNpcDefinitions(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        var incoming = new Dictionary<uint, NpcSpawnDefinition>(
            definitions.Count);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (definition.MapId != MapId)
            {
                throw new ArgumentException(
                    $"NPC {definition.ObjectId} belongs to map " +
                    $"{definition.MapId}, not map {MapId}.",
                    nameof(definitions));
            }

            var clone = CloneNpcDefinition(definition);
            if (!incoming.TryAdd(clone.ObjectId, clone))
            {
                throw new ArgumentException(
                    $"NPC object {clone.ObjectId} occurs more than once.",
                    nameof(definitions));
            }
        }

        return incoming;
    }

    private bool NpcSetMatchesCurrent(
        IReadOnlyDictionary<uint, NpcSpawnDefinition> incoming)
    {
        if (incoming.Count != _authoritativeNpcs.Count ||
            incoming.Count != _npcsByObjectId.Count)
        {
            return false;
        }

        foreach (var pair in incoming)
        {
            if (!_authoritativeNpcs.TryGetValue(
                    pair.Key,
                    out var currentDefinition) ||
                !NpcDefinitionsEqual(currentDefinition, pair.Value) ||
                !_npcsByObjectId.TryGetValue(
                    pair.Key,
                    out var entity) ||
                !_world.IsAlive(entity))
            {
                return false;
            }
        }

        return true;
    }

    private void ValidatePlayerContext(
        GameSessionContext context,
        PlayerTransformOverride? transformOverride)
    {
        if (context.MapId != MapId ||
            (transformOverride is null &&
             context.Character.CurrentMap != MapId) ||
            (transformOverride is { } transform &&
             transform.MapId != MapId))
        {
            throw new ArgumentException(
                $"Player {context.CharacterId} does not belong to map {MapId}.",
                nameof(context));
        }

        if (context.CharacterId != context.Character.Id ||
            context.AccountId != context.Character.AccountId)
        {
            throw new ArgumentException(
                "The map context and character identity do not match.",
                nameof(context));
        }
    }

    private void RecordPlayerFault(
        GameSessionContext context,
        Exception exception)
    {
        _playerFaults[context.Session] =
            $"player object={context.ObjectId} map={MapId}: " +
            $"{exception.GetType().Name}: {exception.Message}";
        _faultCount++;
        _revision++;
    }

    private readonly record struct PlayerBinding(
        EntityId Entity,
        uint ObjectId);
}
