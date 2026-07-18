using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class GameSessionRegistry
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<ClientSession, GameSessionContext> _sessions = [];
    private readonly ConcurrentDictionary<int, ClientSession> _accountSessions = [];
    private readonly ConcurrentDictionary<byte, MapInstance> _maps = [];

    public void JoinMap(
        ClientSession session,
        int accountId,
        GameCharacter character,
        uint objectId,
        bool worldReady = true)
    {
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
            if (_sessions.TryGetValue(session, out previous) && previous.MapId != character.CurrentMap)
            {
                RemoveFromMap(previous);
            }

            _sessions[session] = context;
            AddToMap(context);
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

    public void Remove(ClientSession session)
    {
        GameSessionContext? context;
        lock (_gate)
        {
            if (!_sessions.TryRemove(session, out context))
            {
                return;
            }

            RemoveFromMap(context);
        }

        if (context is null)
        {
            return;
        }

        Console.WriteLine($"[world] left map={context.MapId} character={context.DisplayName} account={context.AccountId} population={GetMapPopulation(context.MapId)}");
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

            if (existing.MapId != updated.MapId)
            {
                RemoveFromMap(existing);
            }

            _sessions[session] = updated;
            AddToMap(updated);
        }
    }

    public bool TryMarkWorldReady(
        ClientSession session,
        IReadOnlyDictionary<uint, long> knownWorldRevisions,
        out IReadOnlyList<GameSessionContext> unseenPlayers)
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
            _sessions[session] = updated;
            AddToMap(updated);
            return true;
        }
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
        DateTimeOffset? initializedAt = null)
    {
        var map = _maps.GetOrAdd(mapId, static id => new MapInstance(id));
        return map.InitializeMonsters(
            definitions,
            initializedAt ?? DateTimeOffset.UtcNow).Count;
    }

    public IReadOnlyList<MonsterRuntimeSnapshot> GetMapMonsterSnapshots(byte mapId)
    {
        return _maps.TryGetValue(mapId, out var map)
            ? map.SnapshotMonsters()
            : [];
    }

    public bool TryGetMonsterSnapshot(
        byte mapId,
        uint objectId,
        out MonsterRuntimeSnapshot snapshot)
    {
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryGetMonsterSnapshot(objectId, out snapshot))
        {
            return true;
        }

        snapshot = default!;
        return false;
    }

    public bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            DateTimeOffset.UtcNow,
            out result);
    }

    internal bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryApplyMonsterDamage(objectId, damage, now, out result))
        {
            return true;
        }

        result = default!;
        return false;
    }

    public ValueTask<MonsterVisibilityTransition?> BeginMonsterVisibilityTransitionAsync(
        ClientSession session,
        byte mapId,
        float playerX,
        float playerZ,
        CancellationToken cancellationToken)
    {
        return _maps.TryGetValue(mapId, out var map)
            ? map.BeginMonsterVisibilityTransitionAsync(
                session,
                playerX,
                playerZ,
                cancellationToken)
            : ValueTask.FromResult<MonsterVisibilityTransition?>(null);
    }

    public bool IsMonsterVisibleTo(ClientSession session, uint objectId)
    {
        return _sessions.TryGetValue(session, out var context) &&
               _maps.TryGetValue(context.MapId, out var map) &&
               map.IsMonsterVisibleTo(session, objectId);
    }

    public async Task<int> BroadcastToMonsterViewersAsync(
        byte mapId,
        uint monsterId,
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
                excludeSession is not null && ReferenceEquals(context.Session, excludeSession) ||
                !map.IsMonsterVisibleTo(context.Session, monsterId))
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

    public async Task RunMonsterRoamingAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonsterMapRuntime.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await AdvanceMonsterWorldOnceAsync(DateTimeOffset.UtcNow, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task AdvanceMonsterWorldOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var map in _maps.Values.OrderBy(map => map.MapId))
        {
            var tick = map.AdvanceMonsters(now);
            if (!tick.PositionsChanged && tick.Updates.Count == 0)
            {
                continue;
            }

            foreach (var context in map.Snapshot())
            {
                if (!context.WorldReady)
                {
                    continue;
                }

                try
                {
                    await SendMonsterRuntimeTickAsync(
                        map,
                        context,
                        tick,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    Remove(context.Session);
                }
            }
        }
    }

    private static async Task SendMonsterRuntimeTickAsync(
        MapInstance map,
        GameSessionContext context,
        MonsterRuntimeTick tick,
        CancellationToken cancellationToken)
    {
        await using var transition = await map.BeginMonsterVisibilityTransitionAsync(
            context.Session,
            context.Character.PositionX,
            context.Character.PositionZ,
            cancellationToken);
        if (transition is null)
        {
            return;
        }

        var delta = transition.Delta;
        var despawnedObjectIds = tick.Updates
            .Where(update => update.Kind == MonsterRuntimeUpdateKind.Despawned)
            .Select(update => update.Monster.ObjectId)
            .ToHashSet();
        var ordinaryLeaving = delta.Leaving
            .Where(objectId => !despawnedObjectIds.Contains(objectId))
            .ToArray();
        if (ordinaryLeaving.Length > 0)
        {
            await context.Session.SendAsync(
                PacketBuilder.RemoveWorldObjects(ordinaryLeaving),
                cancellationToken,
                "RoamingMonsterAoiRemovals");
        }

        foreach (var objectId in delta.Leaving.Where(despawnedObjectIds.Contains))
        {
            await context.Session.SendAsync(
                PacketBuilder.MonsterLifecycleMarker(objectId),
                cancellationToken,
                "MonsterCorpseDespawn");
        }

        var enteringObjectIds = delta.Entering
            .Select(monster => monster.ObjectId)
            .ToHashSet();
        var respawnedObjectIds = tick.Updates
            .Where(update => update.Kind == MonsterRuntimeUpdateKind.Respawned)
            .Select(update => update.Monster.ObjectId)
            .ToHashSet();
        foreach (var objectId in enteringObjectIds.Where(respawnedObjectIds.Contains))
        {
            await context.Session.SendAsync(
                PacketBuilder.MonsterLifecycleMarker(objectId),
                cancellationToken,
                "MonsterRespawnMarker");
        }

        if (delta.Entering.Count > 0)
        {
            await context.Session.SendAsync(
                PacketBuilder.CapturedMonsterSpawns(
                    delta.Entering.Select(monster => monster.Appearance).ToArray()),
                cancellationToken,
                "RoamingMonsterAoiSpawns",
                framed: false);
        }

        // A monster can cross into a stationary viewer's AOI midway through a
        // leg. Start a continuation after its appearance so the new viewer does
        // not see a frozen monster followed by an arrival snap.
        foreach (var monster in delta.Entering.Where(monster => monster.IsMoving))
        {
            await context.Session.SendAsync(
                PacketBuilder.MonsterMovementStart(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    monster.VelocityX,
                    monster.VelocityY,
                    monster.VelocityZ),
                cancellationToken,
                "RoamingMonsterContinuation");
        }

        foreach (var update in tick.Updates)
        {
            var monster = update.Monster;
            if (enteringObjectIds.Contains(monster.ObjectId) ||
                !transition.IsDesiredVisible(monster.ObjectId))
            {
                continue;
            }

            if (update.Kind is MonsterRuntimeUpdateKind.Started or MonsterRuntimeUpdateKind.Arrived &&
                (!map.TryGetMonsterSnapshot(monster.ObjectId, out var currentMonster) ||
                 !currentMonster.IsAlive ||
                 !currentMonster.IsSpawned ||
                 update.Kind == MonsterRuntimeUpdateKind.Started && !currentMonster.IsMoving))
            {
                // Combat can atomically kill a monster after this world tick was
                // calculated but before a slower viewer send. Never resurrect a
                // cancelled leg with a stale movement packet.
                continue;
            }

            var packet = update.Kind switch
            {
                MonsterRuntimeUpdateKind.Started => PacketBuilder.MonsterMovementStart(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    monster.VelocityX,
                    monster.VelocityY,
                    monster.VelocityZ),
                MonsterRuntimeUpdateKind.Arrived => PacketBuilder.MonsterMovementEnd(
                    monster.ObjectId,
                    monster.MovementTicks,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    monster.Facing),
                _ => []
            };
            if (packet.Length > 0)
            {
                await context.Session.SendAsync(
                    packet,
                    cancellationToken,
                    $"RoamingMonster{update.Kind}");
            }
        }

        // Commit only after the complete remove/spawn/movement handoff succeeds.
        transition.Commit();
    }

    private void AddToMap(GameSessionContext context)
    {
        var map = _maps.GetOrAdd(context.MapId, static mapId => new MapInstance(mapId));
        map.AddOrUpdate(context);
    }

    private void EnsureMapObjectIdAvailable(GameSessionContext context)
    {
        if (!_maps.TryGetValue(context.MapId, out var map))
        {
            return;
        }

        var collision = map.Snapshot()
            .FirstOrDefault(candidate =>
                !ReferenceEquals(candidate.Session, context.Session) &&
                candidate.ObjectId == context.ObjectId);
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"World object ID {context.ObjectId} is already assigned to character {collision.CharacterName} on map {context.MapId}.");
        }
    }

    private void RemoveFromMap(GameSessionContext context)
    {
        if (_maps.TryGetValue(context.MapId, out var map))
        {
            map.Remove(context.Session, out _);
        }
    }
}
