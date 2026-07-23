using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const uint LocalPlayerObjectId = 0x00001448;
    internal static readonly TimeSpan PlayerRecoveryInterval = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan ExperienceBoostStatusReconciliationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlayerRecoveryPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<ClientSession, GameSessionContext> _sessions = [];
    private readonly ConcurrentDictionary<int, ClientSession> _accountSessions = [];
    private readonly ConcurrentDictionary<byte, MapInstance> _maps = [];
    private readonly ConcurrentDictionary<int, DateTimeOffset> _nextPlayerRecoveryAt = [];
    private readonly ConcurrentDictionary<ClientSession, PlayerStatusState> _playerStatusStates = [];
    private readonly ConcurrentDictionary<ClientSession, long> _playerLifeRevisions = [];
    private readonly ConcurrentDictionary<ClientSession, ZodiacOnlineSessionState> _zodiacOnlineSessions = [];
    private readonly ConcurrentDictionary<ClientSession, ProgressionBoostOnlineSessionState> _progressionBoostOnlineSessions = [];
    private readonly ConcurrentDictionary<int, ClientSession> _progressionBoostCharacterOwners = [];
    private readonly IGameStore? _store;
    private readonly ZodiacEnergyPolicy _zodiacEnergyPolicy;
    private readonly TimeSpan _zodiacPersistenceInterval;
    private readonly MonsterRuntimeMode _monsterRuntimeMode;

    public GameSessionRegistry(
        IGameStore? store = null,
        ZodiacEnergyOptions? zodiacEnergyOptions = null,
        MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs)
    {
        _store = store;
        if (!Enum.IsDefined(monsterRuntimeMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(monsterRuntimeMode),
                monsterRuntimeMode,
                "Unsupported monster runtime mode.");
        }

        _monsterRuntimeMode = monsterRuntimeMode;
        zodiacEnergyOptions ??= new ZodiacEnergyOptions();
        zodiacEnergyOptions.Normalize();
        _zodiacEnergyPolicy = zodiacEnergyOptions.Snapshot();
        _zodiacPersistenceInterval = TimeSpan.FromSeconds(
            zodiacEnergyOptions.PersistenceIntervalSeconds);
    }

    public void JoinMap(
        ClientSession session,
        int accountId,
        GameCharacter character,
        uint objectId,
        bool worldReady = true,
        DateTimeOffset? joinedAt = null)
    {
        var onlineStartedAt = joinedAt ?? DateTimeOffset.UtcNow;
        var context = new GameSessionContext(
            session,
            accountId,
            character.Id,
            character.Name,
            character.CurrentMap,
            objectId,
            character,
            worldReady,
            0);
        GameSessionContext? previous = null;
        lock (_gate)
        {
            EnsureMapObjectIdAvailable(context);
            var previousMapRemoved =
                _sessions.TryGetValue(session, out previous) &&
                previous.MapId != character.CurrentMap;
            MapInstance.PlayerTransfer? transfer = null;
            if (previousMapRemoved &&
                _playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                transfer = StageMapTransfer(context);
            }
            else if (previousMapRemoved)
            {
                RemoveFromMap(previous!);
            }

            try
            {
                if (transfer is not null)
                {
                    RemoveFromMap(previous!);
                    transfer.Commit(() => _sessions[session] = context);
                }
                else
                {
                    AddToMap(context);
                    _sessions[session] = context;
                }
            }
            catch
            {
                if (transfer is null &&
                    previousMapRemoved &&
                    previous is not null)
                {
                    AddToMap(previous);
                }

                throw;
            }
            finally
            {
                transfer?.Dispose();
            }

            if (previous is not null &&
                previous.CharacterId != character.Id)
            {
                _nextPlayerRecoveryAt.TryRemove(
                    previous.CharacterId,
                    out _);
                RemovePlayerRuntimeEcs(session);
            }

            _playerLifeRevisions.TryAdd(session, 0);
            _nextPlayerRecoveryAt[character.Id] =
                onlineStartedAt + PlayerRecoveryInterval;
            _zodiacOnlineSessions.AddOrUpdate(
                session,
                _ => new ZodiacOnlineSessionState(
                    accountId,
                    character.Id,
                    character,
                    onlineStartedAt),
                (_, existing) =>
                {
                    if (existing.CharacterId == character.Id)
                    {
                        existing.Character = character;
                        return existing;
                    }

                    return new ZodiacOnlineSessionState(
                        accountId,
                        character.Id,
                        character,
                        onlineStartedAt);
                });
            if (worldReady)
            {
                StartProgressionBoostOnlineSession(
                    session,
                    accountId,
                    character.Id,
                    previous?.CharacterId,
                    onlineStartedAt);
            }
        }

        if (previous is null)
        {
            Console.WriteLine($"[world] joined map={context.MapId} character={context.DisplayName} object={context.ObjectId} account={accountId} population={GetMapPopulation(context.MapId)}");
        }
        else if (previous.MapId != context.MapId)
        {
            Console.WriteLine($"[world] moved map={previous.MapId}->{context.MapId} character={context.DisplayName} object={context.ObjectId} account={accountId} population={GetMapPopulation(context.MapId)}");
        }
    }

    public void Remove(ClientSession session, bool preservePlayerStatus = false)
    {
        GameSessionContext? context;
        lock (_gate)
        {
            if (!_sessions.TryRemove(session, out context))
            {
                return;
            }

            RemoveFromMap(context);
            _nextPlayerRecoveryAt.TryRemove(context.CharacterId, out _);
            RemovePlayerRuntimeEcs(session);
            if (!preservePlayerStatus &&
                _playerStatusStates.TryRemove(session, out var statusState))
            {
                statusState.Lifetime.Cancel();
            }

            if (!preservePlayerStatus)
            {
                _playerLifeRevisions.TryRemove(session, out _);
            }
        }

        if (context is null)
        {
            return;
        }

        Console.WriteLine($"[world] left map={context.MapId} character={context.DisplayName} account={context.AccountId} population={GetMapPopulation(context.MapId)}");
    }

    public void RemovePlayerStatusState(ClientSession session)
    {
        if (_playerStatusStates.TryRemove(session, out var statusState))
        {
            statusState.Lifetime.Cancel();
        }

        _playerLifeRevisions.TryRemove(session, out _);
    }

    public void UpdateCharacter(
        ClientSession session,
        GameCharacter character,
        bool advanceWorldRevision = true)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var existing))
            {
                return;
            }

            var updated = existing with
            {
                CharacterId = character.Id,
                CharacterName = character.Name,
                MapId = character.CurrentMap,
                Character = character,
                WorldRevision = advanceWorldRevision
                    ? existing.WorldRevision + 1
                    : existing.WorldRevision
            };

            EnsureMapObjectIdAvailable(updated);

            var previousMapRemoved = existing.MapId != updated.MapId;
            MapInstance.PlayerTransfer? transfer = null;
            if (previousMapRemoved &&
                _playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                transfer = StageMapTransfer(updated);
            }
            else if (previousMapRemoved)
            {
                RemoveFromMap(existing);
            }

            try
            {
                if (transfer is not null)
                {
                    RemoveFromMap(existing);
                    transfer.Commit(() => _sessions[session] = updated);
                }
                else
                {
                    AddToMap(updated);
                    _sessions[session] = updated;
                }
            }
            catch
            {
                if (transfer is null && previousMapRemoved)
                {
                    AddToMap(existing);
                }

                throw;
            }
            finally
            {
                transfer?.Dispose();
            }

            if (_zodiacOnlineSessions.TryGetValue(session, out var zodiacState) &&
                zodiacState.CharacterId == character.Id)
            {
                zodiacState.Character = character;
            }
        }
    }

    public long GetPlayerLifeRevision(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerLifeRevisions.GetOrAdd(session, 0);
    }

    public long AdvancePlayerLifeRevision(ClientSession session) =>
        AdvancePlayerLifeRevision(
            session,
            DateTimeOffset.UtcNow);

    internal long AdvancePlayerLifeRevision(
        ClientSession session,
        DateTimeOffset advancedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            var revision = _playerLifeRevisions.AddOrUpdate(
                session,
                1,
                static (_, revision) => revision + 1);
            if (_sessions.TryGetValue(session, out var context))
            {
                _nextPlayerRecoveryAt[context.CharacterId] =
                    advancedAt + PlayerRecoveryInterval;
                ResetPlayerRecoveryEcs(session);
                ResetPlayerVitalsDamageEcs(session);
            }

            return revision;
        }
    }

    public bool TryMarkWorldReady(
        ClientSession session,
        IReadOnlyDictionary<uint, long> knownWorldRevisions,
        out IReadOnlyList<GameSessionContext> unseenPlayers,
        DateTimeOffset? worldReadyAt = null)
    {
        lock (_gate)
        {
            unseenPlayers = [];
            if (!_sessions.TryGetValue(session, out var existing))
            {
                return false;
            }

            if (existing.WorldReady)
            {
                return true;
            }

            if (_maps.TryGetValue(existing.MapId, out var map))
            {
                unseenPlayers = map.Snapshot()
                    .Where(candidate =>
                        candidate.WorldReady &&
                        !ReferenceEquals(candidate.Session, session) &&
                        (!knownWorldRevisions.TryGetValue(candidate.ObjectId, out var knownRevision) ||
                         knownRevision != candidate.WorldRevision))
                    .ToArray();
                if (unseenPlayers.Count > 0)
                {
                    return false;
                }
            }

            var updated = existing with { WorldReady = true };
            AddToMap(updated);
            _sessions[session] = updated;
            StartProgressionBoostOnlineSession(
                session,
                updated.AccountId,
                updated.CharacterId,
                existing.CharacterId,
                worldReadyAt ?? DateTimeOffset.UtcNow);
            return true;
        }
    }

    private void StartProgressionBoostOnlineSession(
        ClientSession session,
        int accountId,
        int characterId,
        int? previousCharacterId,
        DateTimeOffset onlineStartedAt)
    {
        var boostState = _progressionBoostOnlineSessions.AddOrUpdate(
            session,
            _ => new ProgressionBoostOnlineSessionState(
                accountId,
                characterId,
                onlineStartedAt),
            (_, existing) => existing.CharacterId == characterId
                ? existing
                : new ProgressionBoostOnlineSessionState(
                    accountId,
                    characterId,
                    onlineStartedAt));
        if (previousCharacterId.HasValue &&
            previousCharacterId.Value != boostState.CharacterId)
        {
            _progressionBoostCharacterOwners.TryRemove(
                new KeyValuePair<int, ClientSession>(previousCharacterId.Value, session));
        }

        _progressionBoostCharacterOwners[characterId] = session;
    }

    public ClientSession? ReplaceAccountSession(int accountId, ClientSession session)
    {
        ClientSession? replaced = null;
        _accountSessions.AddOrUpdate(
            accountId,
            session,
            (_, existing) =>
            {
                if (!ReferenceEquals(existing, session))
                {
                    replaced = existing;
                }

                return session;
            });

        return replaced;
    }

    public bool RemoveAccountSession(int accountId, ClientSession session)
    {
        return _accountSessions.TryGetValue(accountId, out var existing)
            && ReferenceEquals(existing, session)
            && _accountSessions.TryRemove(new KeyValuePair<int, ClientSession>(accountId, session));
    }

    public async Task<int> BroadcastToMapAsync(
        byte mapId,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string? label = null,
        bool framed = true)
    {
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return 0;
        }

        var sent = 0;
        foreach (var context in map.Snapshot())
        {
            if (!context.WorldReady ||
                excludeSession is not null && ReferenceEquals(context.Session, excludeSession))
            {
                continue;
            }

            try
            {
                await context.Session.SendAsync(packet, cancellationToken, label, framed);
                sent++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(context.Session);
            }
        }

        return sent;
    }

    public int GetMapPopulation(byte mapId)
    {
        return _maps.TryGetValue(mapId, out var map) ? map.Population : 0;
    }

    public IReadOnlyList<GameSessionContext> GetMapSessions(byte mapId, ClientSession? excludeSession = null)
    {
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return [];
        }

        return map.Snapshot()
            .Where(context =>
                context.WorldReady &&
                (excludeSession is null || !ReferenceEquals(context.Session, excludeSession)))
            .ToArray();
    }

    public bool TryGetMapSessionByObjectId(
        byte mapId,
        uint objectId,
        ClientSession? excludeSession,
        out GameSessionContext context)
    {
        context = default!;
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        foreach (var candidate in map.Snapshot())
        {
            if (!candidate.WorldReady ||
                excludeSession is not null && ReferenceEquals(candidate.Session, excludeSession))
            {
                continue;
            }

            if (candidate.ObjectId != objectId)
            {
                continue;
            }

            context = candidate;
            return true;
        }

        return false;
    }

    public bool TryGetMapSessionByCharacterId(
        byte mapId,
        int characterId,
        ClientSession? excludeSession,
        out GameSessionContext context)
    {
        context = default!;
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        foreach (var candidate in map.Snapshot())
        {
            if (!candidate.WorldReady ||
                excludeSession is not null && ReferenceEquals(candidate.Session, excludeSession))
            {
                continue;
            }

            if (candidate.CharacterId != characterId)
            {
                continue;
            }

            context = candidate;
            return true;
        }

        return false;
    }

    public int InitializeMapMonsters(
        byte mapId,
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        DateTimeOffset? initializedAt = null,
        WorldBossRespawnState? activeWorldBossRespawn = null)
    {
        var map = _maps.GetOrAdd(
            mapId,
            id => new MapInstance(
                id,
                _monsterRuntimeMode,
                _playerRuntimeMode));
        return map.InitializeMonsters(
            definitions,
            initializedAt ?? DateTimeOffset.UtcNow,
            activeWorldBossRespawn).Count;
    }

}
