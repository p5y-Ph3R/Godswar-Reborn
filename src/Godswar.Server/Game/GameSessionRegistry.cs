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
    private readonly ConcurrentDictionary<ClientSession, PlayerStatusState> _playerStatusStates = [];
    private readonly ConcurrentDictionary<ClientSession, ZodiacOnlineSessionState> _zodiacOnlineSessions = [];
    private readonly ConcurrentDictionary<ClientSession, ProgressionBoostOnlineSessionState> _progressionBoostOnlineSessions = [];
    private readonly ConcurrentDictionary<int, ClientSession> _progressionBoostCharacterOwners = [];
    private readonly IGameStore? _store;
    private readonly ZodiacEnergyPolicy _zodiacEnergyPolicy;
    private readonly TimeSpan _zodiacPersistenceInterval;

    public GameSessionRegistry(
        IGameStore? store = null,
        ZodiacEnergyOptions? zodiacEnergyOptions = null)
    {
        _store = store;
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
            if (_sessions.TryGetValue(session, out previous) && previous.MapId != character.CurrentMap)
            {
                RemoveFromMap(previous);
            }

            _sessions[session] = context;
            AddToMap(context);
            _nextPlayerRecoveryAt[character.Id] = DateTimeOffset.UtcNow + PlayerRecoveryInterval;
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
            if (!preservePlayerStatus &&
                _playerStatusStates.TryRemove(session, out var statusState))
            {
                statusState.Lifetime.Cancel();
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
            if (_zodiacOnlineSessions.TryGetValue(session, out var zodiacState) &&
                zodiacState.CharacterId == character.Id)
            {
                zodiacState.Character = character;
            }
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
            _sessions[session] = updated;
            AddToMap(updated);
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
                var boosts = await GetExperienceBoostStateAsync(
                    context.Session,
                    context.AccountId,
                    context.CharacterId,
                    context.Character.Camp,
                    context.MapId,
                    now,
                    cancellationToken);
                if (await RefreshExperienceStatusesAndPublishAsync(
                        context.Session,
                        boosts,
                        now,
                        reason,
                        force: true,
                        broadcast: true,
                        cancellationToken))
                {
                    sent++;
                }
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

    public Task<bool> RefreshExperienceStatusesAndPublishAsync(
        ClientSession session,
        ExperienceBoostState boosts,
        string reason,
        CancellationToken cancellationToken)
    {
        return RefreshExperienceStatusesAndPublishAsync(
            session,
            boosts,
            DateTimeOffset.UtcNow,
            reason,
            force: true,
            broadcast: true,
            cancellationToken);
    }

    internal async Task<bool> RefreshExperienceStatusesAndPublishAsync(
        ClientSession session,
        ExperienceBoostState boosts,
        DateTimeOffset now,
        string reason,
        bool force,
        bool broadcast,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(boosts);

        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            state.ExperienceBoosts = boosts;
            return await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force,
                broadcast,
                cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<bool> ApplyRuntimeStatusAndPublishAsync(
        ClientSession session,
        SkillStatusEffectDefinition definition,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        ActiveRuntimeStatus? appliedStatus = null;
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.RuntimeStatuses.TryGetValue(definition.Kind, out var existing) &&
                existing.ExpiresAt > now &&
                existing.Priority > definition.Priority)
            {
                return false;
            }

            appliedStatus = new ActiveRuntimeStatus(
                definition.StatusId,
                definition.Kind,
                definition.Priority,
                definition.Beneficial,
                now + definition.Duration,
                new ClientStatusAggregate(
                    definition.HitBonus,
                    definition.CriticalAppendBonus,
                    0f),
                checked(++state.Revision),
                definition.PhysicalDamageReduction,
                definition.MagicDamageReduction);
            state.RuntimeStatuses[definition.Kind] = appliedStatus;
            await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force: true,
                broadcast: true,
                cancellationToken);
            return true;
        }
        finally
        {
            state.Gate.Release();
            if (appliedStatus is not null)
            {
                ScheduleRuntimeStatusExpiry(session, state, appliedStatus);
            }
        }
    }

    internal ClientStatusAggregate GetRuntimeStatusAggregate(
        ClientSession session,
        DateTimeOffset now)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return ClientStatusAggregate.Empty;
        }

        state.Gate.Wait();
        try
        {
            var active = state.RuntimeStatuses.Values
                .Where(status => status.ExpiresAt > now)
                .ToArray();
            return new ClientStatusAggregate(
                active.Sum(static status => status.Modifiers.Hit),
                active.Sum(static status => status.Modifiers.CriticalAppend),
                0f);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal decimal GetRuntimePhysicalDamageReduction(
        ClientSession session,
        DateTimeOffset now)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return 0m;
        }

        state.Gate.Wait();
        try
        {
            return Math.Clamp(
                state.RuntimeStatuses.Values
                    .Where(status => status.ExpiresAt > now)
                    .Sum(static status => status.PhysicalDamageReduction),
                0m,
                1m);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task SendStatusSnapshotToViewerAsync(
        GameSessionContext player,
        ClientSession viewer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(viewer);

        PlayerStatusSnapshot snapshot;
        if (_playerStatusStates.TryGetValue(player.Session, out var state))
        {
            await state.Gate.WaitAsync(cancellationToken);
            try
            {
                snapshot = PlayerStatusComposer.Compose(
                    state.ExperienceBoosts,
                    state.RuntimeStatuses.Values,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                state.Gate.Release();
            }
        }
        else
        {
            snapshot = PlayerStatusComposer.Compose(
                ExperienceBoostState.Empty,
                [],
                DateTimeOffset.UtcNow);
        }

        await viewer.SendAsync(
            PacketBuilder.PlayerStatusEffects(
                player.Character,
                player.ObjectId,
                snapshot.Effects,
                snapshot.Aggregate),
            cancellationToken,
            "VisiblePlayerStatusEffects");
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

    public async Task<ExperienceBoostState> GetExperienceBoostStateAsync(
        ClientSession session,
        int accountId,
        int characterId,
        byte camp,
        byte mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return ExperienceBoostState.Empty;
        }

        await CheckpointProgressionBoostOnlineTimeAsync(
            session,
            now,
            cancellationToken);
        return await _store.GetExperienceBoostStateAsync(
            accountId,
            characterId,
            camp,
            mapId,
            now,
            cancellationToken);
    }

    public async Task FinishProgressionBoostOnlineSessionAsync(
        ClientSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_progressionBoostOnlineSessions.TryRemove(session, out var state))
        {
            return;
        }

        try
        {
            if (_progressionBoostCharacterOwners.TryGetValue(state.CharacterId, out var owner) &&
                ReferenceEquals(owner, session))
            {
                await ConsumeProgressionBoostOnlineTimeAsync(
                    state,
                    now,
                    cancellationToken);
            }
        }
        finally
        {
            // Never leak ownership after a failed persistence attempt. The
            // periodic checkpoint limits the unpersisted tail to one cycle,
            // and a replacement session must always be able to take ownership.
            _progressionBoostCharacterOwners.TryRemove(
                new KeyValuePair<int, ClientSession>(state.CharacterId, session));
        }
    }

    private async Task CheckpointProgressionBoostOnlineTimeAsync(
        ClientSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_store is null ||
            !_progressionBoostOnlineSessions.TryGetValue(session, out var state) ||
            !_progressionBoostCharacterOwners.TryGetValue(state.CharacterId, out var owner) ||
            !ReferenceEquals(owner, session))
        {
            return;
        }

        await ConsumeProgressionBoostOnlineTimeAsync(
            state,
            now,
            cancellationToken);
    }

    private async Task ConsumeProgressionBoostOnlineTimeAsync(
        ProgressionBoostOnlineSessionState state,
        DateTimeOffset onlineUntil,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (onlineUntil <= state.LastAccountedAt)
            {
                return;
            }

            await _store.ConsumeCharacterBoostOnlineTimeAsync(
                state.AccountId,
                state.CharacterId,
                state.LastAccountedAt,
                onlineUntil,
                cancellationToken);
            state.LastAccountedAt = onlineUntil;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task RunZodiacEnergyAccrualAsync(CancellationToken cancellationToken)
    {
        if (!_zodiacEnergyPolicy.Enabled || _store is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(_zodiacPersistenceInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await AdvanceZodiacEnergyAccrualOnceAsync(
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<int> AdvanceZodiacEnergyAccrualOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_zodiacEnergyPolicy.Enabled || _store is null)
        {
            return 0;
        }

        var notifications = 0;
        foreach (var context in _sessions.Values.Where(static context => context.WorldReady))
        {
            if (!_zodiacOnlineSessions.TryGetValue(context.Session, out var state))
            {
                continue;
            }

            try
            {
                if (await PersistZodiacOnlineTimeAsync(
                        context.Session,
                        state,
                        now,
                        sendNotification: true,
                        cancellationToken))
                {
                    notifications++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[zodiac] online accrual deferred character={state.Character.Name}: {ex.Message}");
            }
        }

        return notifications;
    }

    public async Task FinishZodiacOnlineSessionAsync(
        ClientSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_zodiacOnlineSessions.TryRemove(session, out var state) ||
            !_zodiacEnergyPolicy.Enabled ||
            _store is null)
        {
            return;
        }

        await PersistZodiacOnlineTimeAsync(
            session,
            state,
            now,
            sendNotification: false,
            cancellationToken);
    }

    private async Task<bool> PersistZodiacOnlineTimeAsync(
        ClientSession session,
        ZodiacOnlineSessionState state,
        DateTimeOffset onlineUntil,
        bool sendNotification,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return false;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (onlineUntil <= state.LastAccountedAt)
            {
                return false;
            }

            var result = await _store.ApplyZodiacOnlineTimeAsync(
                state.AccountId,
                state.CharacterId,
                state.LastAccountedAt,
                onlineUntil,
                _zodiacEnergyPolicy,
                cancellationToken);
            if (result is null)
            {
                return false;
            }

            state.LastAccountedAt = result.LastOnlineAt;
            lock (state.Character.ZodiacSync)
            {
                state.Character.ZodiacEnergy = result.CurrentEnergy;
                state.Character.ZodiacEnergyRemainderX100 = result.CurrentEnergyRemainderX100;
                state.Character.ZodiacOnlineDay = result.OnlineDay;
                state.Character.ZodiacOnlineDurationTicksToday = result.OnlineDurationTicksToday;
                state.Character.ZodiacLastOnlineAt = result.LastOnlineAt;
                state.Character.ZodiacLastCompensationDay = result.LastCompensationDay;
            }

            if (!sendNotification || result.GainedEnergyX100 <= 0)
            {
                return false;
            }

            await session.SendAsync(
                PacketBuilder.ZodiacEnergyIncrease(
                    result.CurrentEnergy,
                    result.GainedEnergyX100),
                cancellationToken,
                result.CompensationApplied
                    ? "ZodiacEnergyCompensation"
                    : "ZodiacEnergyIncrease");
            Console.WriteLine(
                $"[zodiac] energy character={state.Character.Name} gain={result.GainedEnergyX100 / 100m:0.##} total={result.CurrentEnergy}.{result.CurrentEnergyRemainderX100:00} compensation={result.CompensationApplied}");
            return true;
        }
        finally
        {
            state.Gate.Release();
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
                var boosts = await GetExperienceBoostStateAsync(
                    context.Session,
                    context.AccountId,
                    context.CharacterId,
                    context.Character.Camp,
                    context.MapId,
                    now,
                    cancellationToken);
                if (await RefreshExperienceStatusesAndPublishAsync(
                        context.Session,
                        boosts,
                        now,
                        "reconcile",
                        force: false,
                        broadcast: true,
                        cancellationToken))
                {
                    sent++;
                }
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

    private async Task<bool> PublishStatusSnapshotLockedAsync(
        ClientSession session,
        PlayerStatusState state,
        DateTimeOffset now,
        string reason,
        bool force,
        bool broadcast,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(session, out var context))
        {
            return false;
        }

        foreach (var expiredKind in state.RuntimeStatuses
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            state.RuntimeStatuses.Remove(expiredKind);
        }

        var snapshot = PlayerStatusComposer.Compose(
            state.ExperienceBoosts,
            state.RuntimeStatuses.Values,
            now);
        if (!force && string.Equals(
                state.LastFingerprint,
                snapshot.Fingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        await session.SendAsync(
            PacketBuilder.PlayerStatusEffects(
                context.Character,
                snapshot.Effects,
                snapshot.Aggregate),
            cancellationToken,
            "PlayerStatusEffects");

        if (broadcast && context.WorldReady)
        {
            await BroadcastToMapAsync(
                context.MapId,
                PacketBuilder.PlayerStatusEffects(
                    context.Character,
                    context.ObjectId,
                    snapshot.Effects,
                    snapshot.Aggregate),
                cancellationToken,
                session,
                "PlayerStatusEffectsWorld");
        }

        state.LastFingerprint = snapshot.Fingerprint;
        Console.WriteLine(
            $"[status] full sync character={context.DisplayName} reason={reason} count={snapshot.Effects.Count} hit={snapshot.Aggregate.Hit} critical={snapshot.Aggregate.CriticalAppend} exp={snapshot.Aggregate.ExperienceBonus:R}");
        return true;
    }

    private bool TryGetOrCreatePlayerStatusState(
        ClientSession session,
        out PlayerStatusState state)
    {
        lock (_gate)
        {
            if (!_sessions.ContainsKey(session))
            {
                state = null!;
                return false;
            }

            state = _playerStatusStates.GetOrAdd(
                session,
                static _ => new PlayerStatusState());
            return true;
        }
    }

    private void ScheduleRuntimeStatusExpiry(
        ClientSession session,
        PlayerStatusState state,
        ActiveRuntimeStatus expected)
    {
        _ = ExpireRuntimeStatusAsync(session, state, expected);
    }

    private async Task ExpireRuntimeStatusAsync(
        ClientSession session,
        PlayerStatusState state,
        ActiveRuntimeStatus expected)
    {
        try
        {
            var delay = expected.ExpiresAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, state.Lifetime.Token);
            }

            await state.Gate.WaitAsync(state.Lifetime.Token);
            try
            {
                if (!state.RuntimeStatuses.TryGetValue(expected.Kind, out var current) ||
                    current.Revision != expected.Revision ||
                    current.StatusId != expected.StatusId ||
                    current.ExpiresAt != expected.ExpiresAt)
                {
                    return;
                }

                state.RuntimeStatuses.Remove(expected.Kind);
                await PublishStatusSnapshotLockedAsync(
                    session,
                    state,
                    DateTimeOffset.UtcNow,
                    $"status-{expected.StatusId}-expired",
                    force: true,
                    broadcast: true,
                    state.Lifetime.Token);
            }
            finally
            {
                state.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[status] expiry publish failed status={expected.StatusId}: {ex.Message}");
        }
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
            expectedSpawnGeneration: null,
            DateTimeOffset.UtcNow,
            out result);
    }

    public bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int attackerCharacterId,
        uint expectedSpawnGeneration,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration,
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
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            attackerCharacterId: null,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

    internal bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

    internal bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryApplyMonsterDamage(
                objectId,
                damage,
                attackerCharacterId,
                expectedSpawnGeneration,
                now,
                out result))
        {
            return true;
        }

        result = default!;
        return false;
    }

    internal bool TryApplyMonsterStun(
        byte mapId,
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        DateTimeOffset now,
        out MonsterStunResult result)
    {
        return TryApplyMonsterStun(
            mapId,
            objectId,
            attackerCharacterId,
            duration,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

    internal bool TryApplyMonsterStun(
        byte mapId,
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterStunResult result)
    {
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryApplyMonsterStun(
                objectId,
                attackerCharacterId,
                duration,
                expectedSpawnGeneration,
                now,
                out result))
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
        CancellationToken cancellationToken,
        bool forceRefreshVisible = false)
    {
        return _maps.TryGetValue(mapId, out var map)
            ? map.BeginMonsterVisibilityTransitionAsync(
                session,
                playerX,
                playerZ,
                cancellationToken,
                forceRefreshVisible)
            : ValueTask.FromResult<MonsterVisibilityTransition?>(null);
    }

    public bool IsMonsterVisibleTo(ClientSession session, uint objectId)
    {
        return _sessions.TryGetValue(session, out var context) &&
               _maps.TryGetValue(context.MapId, out var map) &&
               map.IsMonsterVisibleTo(session, objectId);
    }

    public bool IsMonsterVisibleTo(
        ClientSession session,
        uint objectId,
        uint spawnGeneration)
    {
        return _sessions.TryGetValue(session, out var context) &&
               _maps.TryGetValue(context.MapId, out var map) &&
               map.IsMonsterVisibleTo(session, objectId, spawnGeneration);
    }

    public async Task<int> BroadcastToMonsterViewersAsync(
        byte mapId,
        uint monsterId,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string? label = null,
        bool framed = true,
        MonsterHealthMutation? healthMutation = null,
        uint? expectedSpawnGeneration = null)
    {
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return 0;
        }

        if (healthMutation is { } mutation && mutation.ObjectId != monsterId)
        {
            throw new ArgumentException(
                $"Health mutation object {mutation.ObjectId} does not match broadcast monster {monsterId}.",
                nameof(healthMutation));
        }

        if (healthMutation is { } versionedHealthMutation &&
            expectedSpawnGeneration is { } expectedGeneration &&
            versionedHealthMutation.SpawnGeneration != expectedGeneration)
        {
            throw new ArgumentException(
                "Health mutation and ordinary delivery generation do not match.",
                nameof(expectedSpawnGeneration));
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
                await using var deliveryLease =
                    healthMutation is { } versionedMutation
                        ? await map.AcquireMonsterViewerHealthDeliveryLeaseAsync(
                            context.Session,
                            [versionedMutation],
                            cancellationToken)
                        : expectedSpawnGeneration is { } versionedGeneration
                            ? await map.AcquireMonsterViewerDeliveryLeaseAsync(
                                context.Session,
                                monsterId,
                                versionedGeneration,
                                cancellationToken)
                            : await map.AcquireMonsterViewerDeliveryLeaseAsync(
                                context.Session,
                                monsterId,
                                cancellationToken);
                if (deliveryLease is null)
                {
                    continue;
                }

                if (deliveryLease.ReconciliationObjectIds.Count > 0)
                {
                    await SendMonsterHealthReconciliationAsync(
                        context.Session,
                        deliveryLease,
                        cancellationToken,
                        label);
                }
                else
                {
                    await context.Session.SendAsync(packet, cancellationToken, label, framed);
                }

                deliveryLease.Commit();
                sent++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(context.Session);
            }
        }

        return sent;
    }

    private static async Task SendMonsterHealthReconciliationAsync(
        ClientSession session,
        MonsterViewerDeliveryLease deliveryLease,
        CancellationToken cancellationToken,
        string? label)
    {
        await session.SendAsync(
            PacketBuilder.RemoveWorldObjects(
                deliveryLease.ReconciliationObjectIds.ToArray()),
            cancellationToken,
            $"{label ?? "MonsterHealth"}ReconcileRemove");
        if (deliveryLease.ReconciliationMonsters.Count == 0)
        {
            return;
        }

        await session.SendAsync(
            PacketBuilder.CapturedMonsterSpawns(
                deliveryLease.ReconciliationMonsters
                    .Select(monster => monster.Appearance)
                    .ToArray()),
            cancellationToken,
            $"{label ?? "MonsterHealth"}ReconcileSpawn",
            framed: false);
        foreach (var monster in deliveryLease.ReconciliationMonsters.Where(
                     monster => monster.IsMoving))
        {
            await session.SendAsync(
                PacketBuilder.MonsterMovementStart(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    monster.VelocityX,
                    monster.VelocityY,
                    monster.VelocityZ),
                cancellationToken,
                $"{label ?? "MonsterHealth"}ReconcileMovement");
        }
    }

    public async Task<bool> DeliverMonsterPacketToViewerAsync(
        ClientSession session,
        byte mapId,
        uint monsterId,
        ReadOnlyMemory<byte> packet,
        uint expectedSpawnGeneration,
        CancellationToken cancellationToken,
        string? label = null,
        bool framed = true)
    {
        if (!_sessions.TryGetValue(session, out var context) ||
            context.MapId != mapId ||
            !context.WorldReady ||
            !_maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        await using var deliveryLease =
            await map.AcquireMonsterViewerDeliveryLeaseAsync(
                session,
                monsterId,
                expectedSpawnGeneration,
                cancellationToken);
        if (deliveryLease is null)
        {
            return false;
        }

        await session.SendAsync(packet, cancellationToken, label, framed);
        deliveryLease.Commit();
        return true;
    }

    public async Task<bool> DeliverMonsterHealthPacketToViewerAsync(
        ClientSession session,
        byte mapId,
        uint monsterId,
        ReadOnlyMemory<byte> packet,
        MonsterHealthMutation healthMutation,
        CancellationToken cancellationToken,
        string? label = null,
        bool framed = true)
    {
        if (healthMutation.ObjectId != monsterId)
        {
            throw new ArgumentException(
                $"Health mutation object {healthMutation.ObjectId} does not match delivery monster {monsterId}.",
                nameof(healthMutation));
        }

        if (!_sessions.TryGetValue(session, out var context) ||
            context.MapId != mapId ||
            !context.WorldReady ||
            !_maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        await using var deliveryLease =
            await map.AcquireMonsterViewerHealthDeliveryLeaseAsync(
                session,
                [healthMutation],
                cancellationToken);
        if (deliveryLease is null)
        {
            return false;
        }

        if (deliveryLease.ReconciliationObjectIds.Count > 0)
        {
            await SendMonsterHealthReconciliationAsync(
                session,
                deliveryLease,
                cancellationToken,
                label);
        }
        else
        {
            await session.SendAsync(packet, cancellationToken, label, framed);
        }

        deliveryLease.Commit();
        return true;
    }

    public async Task<bool> DeliverMonsterAreaDamageToViewerAsync(
        ClientSession session,
        byte mapId,
        uint attackerObjectId,
        uint skillId,
        IReadOnlyList<MonsterAreaDamageBroadcastHit> hits,
        CancellationToken cancellationToken,
        string labelPrefix = "AreaSkillSelf")
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0 ||
            !_sessions.TryGetValue(session, out var context) ||
            context.MapId != mapId ||
            !context.WorldReady ||
            !_maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        var mutations = hits.Select(hit => hit.HealthMutation).ToArray();
        var hitsByObjectId = hits.ToDictionary(hit => hit.HealthMutation.ObjectId);
        await using var deliveryLease =
            await map.AcquireMonsterViewerHealthDeliveryLeaseAsync(
                session,
                mutations,
                cancellationToken);
        if (deliveryLease is null)
        {
            return false;
        }

        if (deliveryLease.ReconciliationObjectIds.Count > 0)
        {
            await SendMonsterHealthReconciliationAsync(
                session,
                deliveryLease,
                cancellationToken,
                labelPrefix);
        }

        if (deliveryLease.DirectHealthMutations.Count > 0)
        {
            var directHits = deliveryLease.DirectHealthMutations
                .Select(mutation => hitsByObjectId[mutation.ObjectId])
                .Select(hit => new SkillClusterDamageEntry(
                    hit.HealthMutation.ObjectId,
                    hit.ReportedDamage))
                .ToArray();
            await session.SendAsync(
                PacketBuilder.SkillClusterDamage(
                    attackerObjectId,
                    skillId,
                    directHits),
                cancellationToken,
                $"{labelPrefix}Damage");
        }

        deliveryLease.Commit();
        return true;
    }

    public async Task<int> BroadcastMonsterAreaDamageToViewersAsync(
        byte mapId,
        ReadOnlyMemory<byte> visualPacket,
        ReadOnlyMemory<byte> impactPacket,
        uint attackerObjectId,
        uint skillId,
        IReadOnlyList<MonsterAreaDamageBroadcastHit> hits,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string labelPrefix = "AreaSkill")
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            var visualRecipients = await BroadcastToMapAsync(
                mapId,
                visualPacket,
                cancellationToken,
                excludeSession,
                $"{labelPrefix}CastWorld");
            var impactRecipients = await BroadcastToMapAsync(
                mapId,
                impactPacket,
                cancellationToken,
                excludeSession,
                $"{labelPrefix}ImpactWorld");
            return Math.Max(visualRecipients, impactRecipients);
        }

        if (!_maps.TryGetValue(mapId, out var map))
        {
            return 0;
        }

        var mutations = hits.Select(hit => hit.HealthMutation).ToArray();
        var hitsByObjectId = hits.ToDictionary(
            hit => hit.HealthMutation.ObjectId);
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
                await using var deliveryLease =
                    await map.AcquireMonsterViewerHealthDeliveryLeaseAsync(
                        context.Session,
                        mutations,
                        cancellationToken);
                if (deliveryLease is null)
                {
                    continue;
                }

                if (deliveryLease.ReconciliationObjectIds.Count > 0)
                {
                    await SendMonsterHealthReconciliationAsync(
                        context.Session,
                        deliveryLease,
                        cancellationToken,
                        labelPrefix);
                }

                if (deliveryLease.DirectHealthMutations.Count > 0)
                {
                    var directHits = deliveryLease.DirectHealthMutations
                        .Select(mutation => hitsByObjectId[mutation.ObjectId])
                        .Select(hit => new SkillClusterDamageEntry(
                            hit.HealthMutation.ObjectId,
                            hit.ReportedDamage))
                        .ToArray();
                    await context.Session.SendAsync(
                        visualPacket,
                        cancellationToken,
                        $"{labelPrefix}CastWorld");
                    await context.Session.SendAsync(
                        impactPacket,
                        cancellationToken,
                        $"{labelPrefix}ImpactWorld");
                    await context.Session.SendAsync(
                        PacketBuilder.SkillClusterDamage(
                            attackerObjectId,
                            skillId,
                            directHits),
                        cancellationToken,
                        $"{labelPrefix}DamageWorld");
                }

                deliveryLease.Commit();
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
        var returnedByObjectId = tick.Updates
            .Where(update => update.Kind == MonsterRuntimeUpdateKind.Returned)
            .GroupBy(update => update.Monster.ObjectId)
            .ToDictionary(group => group.Key, group => group.Last());
        var returnedInsideViewerAoi = new HashSet<uint>();
        var returnedOutsideViewerAoi = new HashSet<uint>();
        foreach (var objectId in delta.Leaving.Where(despawnedObjectIds.Contains))
        {
            if (!returnedByObjectId.TryGetValue(objectId, out var returned))
            {
                continue;
            }

            if (!WorldSectorVisibilityTracker<CapturedMonsterSpawn>.TryGetCell(
                    returned.Monster.X,
                    returned.Monster.Z,
                    out var returnedCell) ||
                !WorldSectorVisibilityTracker<CapturedMonsterSpawn>.IsNeighbor(
                    delta.PlayerCell,
                    returnedCell))
            {
                returnedOutsideViewerAoi.Add(objectId);
                continue;
            }

            // The runtime has already retired this object, so the final-state
            // visibility delta alone would send its marker first and suppress
            // movement-end. Serialize the immutable home-arrival snapshot
            // before removing the old client entity.
            await context.Session.SendAsync(
                PacketBuilder.MonsterMovementEnd(
                    objectId,
                    returned.MovementEndField ?? returned.Monster.MovementTicks,
                    returned.Monster.X,
                    returned.Monster.Y,
                    returned.Monster.Z,
                    returned.Monster.Facing),
                cancellationToken,
                "MonsterLeashReturnEnd");
            await context.Session.SendAsync(
                PacketBuilder.MonsterLifecycleMarker(objectId),
                cancellationToken,
                "MonsterLeashRetire");
            returnedInsideViewerAoi.Add(objectId);
        }

        var ordinaryLeaving = delta.Leaving
            .Where(objectId =>
                !despawnedObjectIds.Contains(objectId) ||
                returnedOutsideViewerAoi.Contains(objectId))
            .ToArray();
        if (ordinaryLeaving.Length > 0)
        {
            await context.Session.SendAsync(
                PacketBuilder.RemoveWorldObjects(ordinaryLeaving),
                cancellationToken,
                "RoamingMonsterAoiRemovals");
        }

        foreach (var objectId in delta.Leaving.Where(objectId =>
                     despawnedObjectIds.Contains(objectId) &&
                     !returnedInsideViewerAoi.Contains(objectId) &&
                     !returnedOutsideViewerAoi.Contains(objectId)))
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

            if (update.Kind is (MonsterRuntimeUpdateKind.Started or
                    MonsterRuntimeUpdateKind.Arrived or
                    MonsterRuntimeUpdateKind.Returned) &&
                (!map.TryGetMonsterSnapshot(monster.ObjectId, out var currentMonster) ||
                 !currentMonster.IsAlive ||
                 !currentMonster.IsSpawned ||
                 currentMonster.SpawnGeneration != monster.SpawnGeneration ||
                 (update.Kind == MonsterRuntimeUpdateKind.Started && !currentMonster.IsMoving) ||
                 (update.Kind == MonsterRuntimeUpdateKind.Returned &&
                  (currentMonster.IsMoving ||
                   currentMonster.CombatPhase != MonsterCombatPhase.AwaitingRetirement))))
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
                MonsterRuntimeUpdateKind.Arrived or MonsterRuntimeUpdateKind.Returned =>
                    PacketBuilder.MonsterMovementEnd(
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
        var statusContext = map.Snapshot().FirstOrDefault(context =>
            context.WorldReady && context.CharacterId == targetCharacterId);
        var damageResolvedAt = DateTimeOffset.UtcNow;
        // Runtime statuses have their own gate. Snapshot the mitigation before
        // taking the registry gate so status publication and a monster attack
        // cannot acquire those locks in opposite order.
        var physicalDamageReduction = statusContext is null
            ? 0m
            : GetRuntimePhysicalDamageReduction(statusContext.Session, damageResolvedAt);
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
                if (statusContext is null ||
                    !ReferenceEquals(statusContext.Session, targetContext.Session))
                {
                    physicalDamageReduction = 0m;
                }

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
                            targetContext.Character,
                            physicalDamageReduction);
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

    private sealed class PlayerStatusState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Dictionary<int, ActiveRuntimeStatus> RuntimeStatuses { get; } = [];

        public ExperienceBoostState ExperienceBoosts { get; set; } = ExperienceBoostState.Empty;

        public string? LastFingerprint { get; set; }

        public long Revision { get; set; }

        public CancellationTokenSource Lifetime { get; } = new();
    }

    private sealed class ZodiacOnlineSessionState(
        int accountId,
        int characterId,
        GameCharacter character,
        DateTimeOffset lastAccountedAt)
    {
        public int AccountId { get; } = accountId;

        public int CharacterId { get; } = characterId;

        public GameCharacter Character { get; set; } = character;

        public DateTimeOffset LastAccountedAt { get; set; } = lastAccountedAt;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class ProgressionBoostOnlineSessionState(
        int accountId,
        int characterId,
        DateTimeOffset lastAccountedAt)
    {
        public int AccountId { get; } = accountId;

        public int CharacterId { get; } = characterId;

        public DateTimeOffset LastAccountedAt { get; set; } = lastAccountedAt;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

internal readonly record struct MonsterAreaDamageBroadcastHit(
    MonsterHealthMutation HealthMutation,
    uint ReportedDamage);
