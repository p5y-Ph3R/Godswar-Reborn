using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class GameSessionRegistry
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
    private readonly ConcurrentDictionary<ClientSession, string> _experienceBoostStatusFingerprints = [];
    private readonly IGameStore? _store;

    public GameSessionRegistry(IGameStore? store = null)
    {
        _store = store;
    }

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
            _nextPlayerRecoveryAt[character.Id] = DateTimeOffset.UtcNow + PlayerRecoveryInterval;
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
            _nextPlayerRecoveryAt.TryRemove(context.CharacterId, out _);
            _experienceBoostStatusFingerprints.TryRemove(session, out _);
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
        DateTimeOffset? initializedAt = null,
        WorldBossRespawnState? activeWorldBossRespawn = null)
    {
        var map = _maps.GetOrAdd(mapId, static id => new MapInstance(id));
        return map.InitializeMonsters(
            definitions,
            initializedAt ?? DateTimeOffset.UtcNow,
            activeWorldBossRespawn).Count;
    }

    public async Task<int> SendExperienceBoostStatusesAsync(
        byte mapId,
        byte? camp,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_store is null || !_maps.TryGetValue(mapId, out var map))
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var sent = 0;
        foreach (var context in map.Snapshot().Where(context =>
                     context.WorldReady &&
                     (camp is null || context.Character.Camp == camp.Value)))
        {
            try
            {
                var boosts = await _store.GetExperienceBoostStateAsync(
                    context.AccountId,
                    context.CharacterId,
                    context.Character.Camp,
                    context.MapId,
                    now,
                    cancellationToken);
                var effects = boosts.ActiveBoosts
                    .Select(boost => new ClientStatusEffect(
                        checked((uint)boost.StatusId),
                        boost.RemainingSeconds(now)))
                    .ToArray();
                await context.Session.SendAsync(
                    PacketBuilder.PlayerStatusEffects(
                        effects,
                        boosts.TotalBonusBasisPoints / 10_000f),
                    cancellationToken,
                    "PlayerStatusEffects");
                RememberExperienceBoostStatus(context.Session, boosts);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[status] EXP boost map sync failed character={context.DisplayName} reason={reason}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[status] EXP boost map sync map={mapId} camp={(camp?.ToString() ?? "all")} reason={reason} sent={sent}");
        return sent;
    }

    public void RememberExperienceBoostStatus(
        ClientSession session,
        ExperienceBoostState boosts)
    {
        _experienceBoostStatusFingerprints[session] = BuildExperienceBoostStatusFingerprint(boosts);
    }

    public async Task RunExperienceBoostStatusReconciliationAsync(
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ExperienceBoostStatusReconciliationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ReconcileExperienceBoostStatusesOnceAsync(
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<int> ReconcileExperienceBoostStatusesOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return 0;
        }

        var sent = 0;
        foreach (var context in _sessions.Values.Where(static context => context.WorldReady))
        {
            try
            {
                var boosts = await _store.GetExperienceBoostStateAsync(
                    context.AccountId,
                    context.CharacterId,
                    context.Character.Camp,
                    context.MapId,
                    now,
                    cancellationToken);
                var fingerprint = BuildExperienceBoostStatusFingerprint(boosts);
                if (_experienceBoostStatusFingerprints.TryGetValue(
                        context.Session,
                        out var previousFingerprint) &&
                    string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                var effects = boosts.ActiveBoosts
                    .Select(boost => new ClientStatusEffect(
                        checked((uint)boost.StatusId),
                        boost.RemainingSeconds(now)))
                    .ToArray();
                await context.Session.SendAsync(
                    PacketBuilder.PlayerStatusEffects(
                        effects,
                        boosts.TotalBonusBasisPoints / 10_000f),
                    cancellationToken,
                    "PlayerStatusEffectsReconcile");
                _experienceBoostStatusFingerprints[context.Session] = fingerprint;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[status] EXP boost reconciliation failed character={context.DisplayName}: {ex.Message}");
            }
        }

        if (sent > 0)
        {
            Console.WriteLine($"[status] EXP boost reconciliation updated={sent}");
        }

        return sent;
    }

    private static string BuildExperienceBoostStatusFingerprint(ExperienceBoostState boosts)
    {
        return string.Join(
            '|',
            boosts.ActiveBoosts
                .OrderBy(static boost => boost.Kind)
                .ThenBy(static boost => boost.StatusId)
                .Select(static boost =>
                    $"{boost.StatusId}:{boost.Kind}:{boost.BonusBasisPoints}:{boost.Priority}:" +
                    $"{boost.ExpiresAt?.UtcTicks ?? long.MaxValue}:{boost.Source}"));
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

    public bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int attackerCharacterId,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            attackerCharacterId,
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
        return TryApplyMonsterDamage(mapId, objectId, damage, null, now, out result);
    }

    internal bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryApplyMonsterDamage(objectId, damage, attackerCharacterId, now, out result))
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

    public async Task RunPlayerRecoveryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PlayerRecoveryPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await AdvancePlayerRecoveryOnceAsync(DateTimeOffset.UtcNow, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task AdvancePlayerRecoveryOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var recovered = new List<GameSessionContext>();
        foreach (var snapshot in _sessions.Values)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(snapshot.Session, out var current) ||
                    !current.WorldReady ||
                    !_nextPlayerRecoveryAt.TryGetValue(current.CharacterId, out var nextRecoveryAt) ||
                    now < nextRecoveryAt)
                {
                    continue;
                }

                _nextPlayerRecoveryAt[current.CharacterId] = now + PlayerRecoveryInterval;
                if (PlayerRecoveryCatalog.TryApply(current.Character))
                {
                    recovered.Add(current);
                }
            }
        }

        foreach (var context in recovered)
        {
            var character = context.Character;
            int currentHp;
            int currentMp;
            long vitalsRevision;
            lock (character.VitalsSync)
            {
                currentHp = character.CurrentHp;
                currentMp = character.CurrentMp;
            }

            try
            {
                await context.Session.SendAsync(
                    PacketBuilder.PlayerVitalsUpdate(
                        LocalPlayerObjectId,
                        currentHp,
                        currentMp),
                    cancellationToken,
                    "PlayerPassiveRecoverySelf");
                await BroadcastToMapAsync(
                    character.CurrentMap,
                    PacketBuilder.PlayerVitalsUpdate(
                        context.ObjectId,
                        currentHp,
                        currentMp),
                    cancellationToken,
                    context.Session,
                    "PlayerPassiveRecoveryWorld");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(context.Session);
            }

            if (_store is not null)
            {
                try
                {
                    lock (character.VitalsSync)
                    {
                        currentHp = character.CurrentHp;
                        currentMp = character.CurrentMp;
                        vitalsRevision = character.VitalsRevision;
                    }

                    await _store.SaveCharacterVitalsAsync(
                        context.AccountId,
                        context.CharacterId,
                        currentHp,
                        currentMp,
                        vitalsRevision,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine(
                        $"[recovery] vitals persistence deferred character={context.DisplayName}: {ex.Message}");
                }
            }

            lock (character.VitalsSync)
            {
                currentHp = character.CurrentHp;
                currentMp = character.CurrentMp;
            }

            Console.WriteLine(
                $"[recovery] character={context.DisplayName} hp={currentHp}/{character.MaxHp} mp={currentMp}/{character.MaxMp}");
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

            foreach (var attack in tick.Updates.Where(update => update.Kind == MonsterRuntimeUpdateKind.Attacked))
            {
                await ProcessMonsterAttackAsync(map, attack, cancellationToken);
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
                    monster.VelocityZ,
                    update.MovementMode),
                MonsterRuntimeUpdateKind.Arrived => PacketBuilder.MonsterMovementEnd(
                    monster.ObjectId,
                    update.MovementEndField ?? monster.MovementTicks,
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

    private async Task ProcessMonsterAttackAsync(
        MapInstance map,
        MonsterRuntimeUpdate attack,
        CancellationToken cancellationToken)
    {
        if (attack.TargetCharacterId is not { } targetCharacterId)
        {
            return;
        }

        GameSessionContext? targetContext;
        uint damage;
        var killed = false;
        lock (_gate)
        {
            targetContext = map.Snapshot().FirstOrDefault(context =>
                context.WorldReady && context.CharacterId == targetCharacterId);
            if (targetContext is null)
            {
                damage = 0;
            }
            else
            {
                lock (targetContext.Character.VitalsSync)
                {
                    if (targetContext.Character.CurrentHp <= 0)
                    {
                        targetContext = null;
                        damage = 0;
                    }
                    else
                    {
                        damage = MonsterCombatResolver.CalculateMonsterPhysicalAttack(
                            attack.Monster.Definition.Tier,
                            targetContext.Character);
                        var beforeHealth = targetContext.Character.CurrentHp;
                        targetContext.Character.CurrentHp = damage >= (uint)beforeHealth
                            ? 0
                            : beforeHealth - (int)damage;
                        targetContext.Character.MarkVitalsChanged();
                        killed = targetContext.Character.CurrentHp == 0;
                    }
                }
            }
        }

        if (targetContext is null || damage == 0)
        {
            map.ClearMonsterAggroForCharacter(targetCharacterId, DateTimeOffset.UtcNow);
            return;
        }

        var monster = attack.Monster;
        var target = targetContext.Character;
        var worldTargetObjectId = WorldObjectIds.ForPlayer(target.Id);
        try
        {
            await targetContext.Session.SendAsync(
                PacketBuilder.SkillCastImpact(
                    monster.ObjectId,
                    LocalPlayerObjectId,
                    2000,
                    attack.TargetX,
                    attack.TargetZ),
                cancellationToken,
                "MonsterAttackImpactSelf");
            await targetContext.Session.SendAsync(
                PacketBuilder.PhysicalDamage(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    LocalPlayerObjectId,
                    damage,
                    result: 0),
                cancellationToken,
                "MonsterAttackDamageSelf");
            if (killed)
            {
                await targetContext.Session.SendAsync(
                    PacketBuilder.PlayerDeath(
                        LocalPlayerObjectId,
                        target.PositionX,
                        0f,
                        target.PositionZ,
                        target.CurrentMap),
                    cancellationToken,
                    "MonsterKillPlayerSelf");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Remove(targetContext.Session);
        }

        foreach (var observer in map.Snapshot())
        {
            if (!observer.WorldReady ||
                ReferenceEquals(observer.Session, targetContext.Session) ||
                !map.IsMonsterVisibleTo(observer.Session, monster.ObjectId))
            {
                continue;
            }

            try
            {
                await observer.Session.SendAsync(
                    PacketBuilder.SkillCastImpact(
                        monster.ObjectId,
                        worldTargetObjectId,
                        2000,
                        attack.TargetX,
                        attack.TargetZ),
                    cancellationToken,
                    "MonsterAttackImpactWorld");
                await observer.Session.SendAsync(
                    PacketBuilder.PhysicalDamage(
                        monster.ObjectId,
                        monster.X,
                        monster.Y,
                        monster.Z,
                        worldTargetObjectId,
                        damage,
                        result: 0),
                    cancellationToken,
                    "MonsterAttackDamageWorld");
                if (killed)
                {
                    await observer.Session.SendAsync(
                        PacketBuilder.PlayerDeath(
                            worldTargetObjectId,
                            target.PositionX,
                            0f,
                            target.PositionZ,
                            target.CurrentMap),
                        cancellationToken,
                        "MonsterKillPlayerWorld");
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(observer.Session);
            }
        }

        if (killed)
        {
            map.ClearMonsterAggroForCharacter(targetCharacterId, DateTimeOffset.UtcNow);
        }

        if (_store is not null)
        {
            try
            {
                int currentHp;
                int currentMp;
                long vitalsRevision;
                lock (target.VitalsSync)
                {
                    currentHp = target.CurrentHp;
                    currentMp = target.CurrentMp;
                    vitalsRevision = target.VitalsRevision;
                }

                await _store.SaveCharacterVitalsAsync(
                    targetContext.AccountId,
                    targetContext.CharacterId,
                    currentHp,
                    currentMp,
                    vitalsRevision,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[monster] victim vitals persistence deferred character={targetContext.DisplayName}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[monster] attack monster={monster.ObjectId} tier={monster.Definition.Tier} target={targetContext.DisplayName} damage={damage} hp={target.CurrentHp}/{target.MaxHp} killed={killed}");
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
            map.ClearMonsterAggroForCharacter(context.CharacterId, DateTimeOffset.UtcNow);
        }
    }
}
