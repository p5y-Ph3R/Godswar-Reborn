using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Zodiac;
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
    private readonly ConcurrentDictionary<
        int,
        PlayerRecoveryDeadline> _nextPlayerRecoveryAt = [];
    private readonly ConcurrentDictionary<ClientSession, PlayerStatusState> _playerStatusStates = [];
    private readonly ConcurrentDictionary<ClientSession, long> _playerLifeRevisions = [];
    private long _nextWorldMembershipEpoch;
    private readonly ConcurrentDictionary<ClientSession, ZodiacOnlineSessionState> _zodiacOnlineSessions = [];
    private readonly ConcurrentDictionary<ClientSession, ProgressionBoostOnlineSessionState> _progressionBoostOnlineSessions = [];
    private readonly ConcurrentDictionary<int, ClientSession> _progressionBoostCharacterOwners = [];
    private readonly ICharacterCheckpointCoordinator? _checkpointCoordinator;
    private readonly IGameStore? _store;
    private readonly IZodiacLevelStore? _zodiacLevelStore;
    private readonly IExperienceBoostStateReader? _experienceBoosts;
    private readonly ZodiacEnergyPolicy _zodiacEnergyPolicy;
    private readonly TimeSpan _zodiacPersistenceInterval;
    private readonly MonsterRuntimeMode _monsterRuntimeMode;
    private readonly GameplayRuntimeCatalogs _gameplayCatalogs;
    private readonly GameplayItemContent? _itemContent;
    private readonly TrainingDummyPolicy _trainingDummies;

    public GameSessionRegistry(
        IGameStore? store = null,
        ZodiacEnergyOptions? zodiacEnergyOptions = null,
        MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs,
        ICharacterCheckpointCoordinator? checkpointCoordinator = null,
        IProgressionIntervalSettlementCommandExecutor?
            progressionIntervalSettlementCommands = null,
        IZodiacLevelStore? zodiacLevelStore = null,
        IExperienceBoostStateReader? experienceBoosts = null,
        bool requiresDurablePlayerPersistence = false,
        WorldInstanceRuntimeOptions? worldInstanceOptions = null,
        GameplayRuntimeCatalogs? gameplayCatalogs = null,
        GameplayItemContent? itemContent = null,
        TrainingDummyPolicy? trainingDummies = null)
    {
        _worldInstanceOptions = SnapshotWorldInstanceOptions(
            worldInstanceOptions);
        _medusaPeriodicDamageLedger = new(
            _worldInstanceOptions.MaximumRuntimes);
        var persistence = ResolveFocusedPersistence(
            store,
            zodiacLevelStore,
            experienceBoosts);
        _store = persistence.LegacyStore;
        _checkpointCoordinator = checkpointCoordinator;
        _progressionIntervalSettlementCommands =
            progressionIntervalSettlementCommands;
        _zodiacLevelStore = persistence.ZodiacLevels;
        _experienceBoosts = persistence.ExperienceBoosts;
        _requiresDurablePlayerPersistence =
            requiresDurablePlayerPersistence;
        ValidateDurablePlayerPersistenceComposition();
        if (!Enum.IsDefined(monsterRuntimeMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(monsterRuntimeMode),
                monsterRuntimeMode,
                "Unsupported monster runtime mode.");
        }

        _monsterRuntimeMode = monsterRuntimeMode;
        _gameplayCatalogs = gameplayCatalogs ??
            GameplayRuntimeCatalogs.Empty;
        _itemContent = itemContent;
        _trainingDummies = trainingDummies ?? TrainingDummyPolicy.Disabled;
        zodiacEnergyOptions ??= new ZodiacEnergyOptions();
        zodiacEnergyOptions.Normalize();
        _zodiacEnergyPolicy = zodiacEnergyOptions.Snapshot();
        _zodiacPersistenceInterval = TimeSpan.FromSeconds(
            zodiacEnergyOptions.PersistenceIntervalSeconds);
    }

    internal GameplayRuntimeCatalogs GameplayCatalogs =>
        _gameplayCatalogs;

    private GameplayItemContent RequireItemContent() =>
        _itemContent ?? throw new InvalidOperationException(
            "Mount gameplay requires a pinned item-content revision.");

    private static (
        T? LegacyStore,
        IZodiacLevelStore? ZodiacLevels,
        IExperienceBoostStateReader? ExperienceBoosts)
        ResolveFocusedPersistence<T>(
            T? broadStore,
            IZodiacLevelStore? zodiacLevels,
            IExperienceBoostStateReader? experienceBoosts)
        where T : class =>
        (
            broadStore,
            zodiacLevels ?? broadStore as IZodiacLevelStore,
            experienceBoosts ??
                broadStore as IExperienceBoostStateReader);

    public void JoinMap(
        ClientSession session,
        int accountId,
        GameCharacter character,
        uint objectId,
        bool worldReady = true,
        DateTimeOffset? joinedAt = null)
        => JoinWorldInstanceCore(
            session,
            accountId,
            character,
            objectId,
            GetOrCreateDefaultWorldInstance(
                character.CurrentMap),
            worldReady,
            joinedAt);

    public void Remove(ClientSession session, bool preservePlayerStatus = false)
    {
        RemoveCore(
            session,
            expectedOwnership: null,
            preservePlayerStatus);
    }

    public bool Remove(
        ClientSession session,
        PlayerOwnershipFence expectedOwnership,
        bool preservePlayerStatus = false) =>
        RemoveCore(
            session,
            expectedOwnership,
            preservePlayerStatus);

    private bool RemoveCore(
        ClientSession session,
        PlayerOwnershipFence? expectedOwnership,
        bool preservePlayerStatus)
    {
        GameSessionContext? context;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out context) ||
                expectedOwnership is { } ownership &&
                context.Ownership != ownership ||
                !_sessions.TryRemove(
                    new KeyValuePair<
                        ClientSession,
                        GameSessionContext>(
                        session,
                        context)))
            {
                return false;
            }

            try
            {
                RemoveFromMap(context);
                ReleaseWorldPlacement(context);
            }
            catch
            {
                AddToMap(context);
                _sessions[session] = context;
                throw;
            }
            _nextPlayerRecoveryAt.TryRemove(context.CharacterId, out _);
            RemoveElementalCombatSession(session);
            ClearTrainingDummyHostileStatusesLocked(session);
            RemovePlayerRuntimeEcs(session);
            if (!preservePlayerStatus &&
                _playerStatusStates.TryRemove(session, out var statusState))
            {
                statusState.Lifetime.Cancel();
            }

            if (!preservePlayerStatus)
            {
                _playerLifeRevisions.TryRemove(session, out _);
                RemovePartySessionLocked(context.CharacterId);
            }
        }

        if (context is null)
        {
            return false;
        }

        Console.WriteLine(
            $"[world] left realm={context.RealmId} " +
            $"instance={context.WorldInstanceId} map={context.MapId} " +
            $"character={context.DisplayName} account={context.AccountId} " +
            $"population={GetWorldInstancePopulation(context.WorldInstanceId)}");
        return true;
    }

    public void RemovePlayerStatusState(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        PlayerStatusState? statusState;
        lock (_gate)
        {
            _ = _playerStatusStates.TryRemove(
                session,
                out statusState);
            _playerLifeRevisions.TryRemove(session, out _);
        }

        statusState?.Lifetime.Cancel();
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

            if (character.RealmId != existing.RealmId)
            {
                throw new InvalidOperationException(
                    "A character update cannot change its active realm.");
            }

            var ownership = PlayerOwnership(character);
            if (existing.Ownership.IsValid &&
                (ownership != existing.Ownership ||
                 !IsCurrentAccountSession(
                     existing.AccountId,
                     session,
                     ownership)))
            {
                return;
            }

            var worldInstance =
                existing.MapId == character.CurrentMap
                    ? GetRequiredWorldInstance(existing)
                    : GetOrCreateDefaultWorldInstance(
                        character.CurrentMap);
            if (worldInstance.RealmId != character.RealmId)
            {
                throw new InvalidOperationException(
                    "The character realm does not match its world " +
                    "instance.");
            }
            var instanceChanged =
                existing.WorldInstanceId != worldInstance.InstanceId;
            var updated = existing with
            {
                CharacterId = character.Id,
                CharacterName = character.Name,
                RealmId = worldInstance.RealmId,
                WorldInstanceId = worldInstance.InstanceId,
                MapId = character.CurrentMap,
                Character = character,
                WorldRevision = advanceWorldRevision
                    ? existing.WorldRevision + 1
                    : existing.WorldRevision,
                WorldMembershipEpoch = instanceChanged
                    ? NextWorldMembershipEpochLocked()
                    : existing.WorldMembershipEpoch
            };

            if (updated == existing)
            {
                // Routine same-character refreshes still have to project the
                // mutable character into the map/ECS shadow. Preserve the
                // immutable membership record itself, though: replacing a
                // value-equal context would manufacture a false routing epoch
                // and suppress an already-committed exact combat/status
                // publication. Real joins/transfers/reacquisitions continue
                // to replace it and therefore retain ReferenceEquals fencing.
                AddToMap(existing);
                return;
            }

            EnsureMapObjectIdAvailable(updated);

            var placementChange =
                PrepareWorldPlacement(existing, updated);
            WorldInstancePlayerTransfer? transfer = null;
            var sourceRemoved = false;
            try
            {
                if (instanceChanged &&
                    _playerRuntimeMode ==
                        PlayerRuntimeMode.Ecs)
                {
                    transfer = StageMapTransfer(updated);
                }
                if (instanceChanged)
                {
                    RemoveFromMap(existing);
                    sourceRemoved = true;
                }

                if (transfer is not null)
                {
                    transfer.Commit(
                        () => _sessions[session] = updated);
                }
                else
                {
                    AddToMap(updated);
                    _sessions[session] = updated;
                }
            }
            catch
            {
                if (sourceRemoved)
                {
                    AddToMap(existing);
                    _sessions[session] = existing;
                }

                RollBackWorldPlacement(
                    placementChange,
                    existing,
                    updated);
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
        return _playerLifeRevisions.TryGetValue(
            session,
            out var lifeRevision)
            ? lifeRevision
            : -1;
    }

    public bool TryGetPlayerLifeRevision(
        ClientSession session,
        out long lifeRevision)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerLifeRevisions.TryGetValue(
            session,
            out lifeRevision);
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
            if (!_sessions.ContainsKey(session) ||
                !_playerLifeRevisions.TryGetValue(
                    session,
                    out var currentRevision))
            {
                return -1;
            }
            var nextRecoveryAt =
                advancedAt + PlayerRecoveryInterval;
            var recoveryDeadline =
                GetOrCreatePlayerRecoveryDeadlineLocked(
                    session);
            var revision = checked(currentRevision + 1);
            if (!_playerLifeRevisions.TryUpdate(
                    session,
                    revision,
                    currentRevision))
            {
                return -1;
            }
            ApplyPlayerLifeAdvanceSideEffectsLocked(
                session,
                nextRecoveryAt,
                recoveryDeadline,
                advancedAt,
                resetIncomingDamage: true);

            return revision;
        }
    }

    private void ApplyPlayerLifeAdvanceSideEffectsLocked(
        ClientSession session,
        DateTimeOffset nextRecoveryAt,
        PlayerRecoveryDeadline recoveryDeadline,
        DateTimeOffset lifeAdvancedAt,
        bool resetIncomingDamage)
    {
        if (!_sessions.TryGetValue(session, out var context))
        {
            return;
        }

        ClearBoundMedusaEffectsForExpiredLifeLocked(
            context,
            lifeAdvancedAt);
        ClearElementalCombatLifeState(session);
        ClearTrainingDummyHostileStatusesLocked(session);
        recoveryDeadline.Write(nextRecoveryAt);
        ResetPlayerRecoveryEcs(session);
        if (resetIncomingDamage)
        {
            ResetPlayerVitalsDamageEcs(session);
        }
    }

    private PlayerRecoveryDeadline
        GetOrCreatePlayerRecoveryDeadlineLocked(
            ClientSession session)
    {
        if (!_sessions.TryGetValue(session, out var context))
        {
            throw new InvalidOperationException(
                "Player recovery requires a joined session.");
        }

        return _nextPlayerRecoveryAt.GetOrAdd(
            context.CharacterId,
            static _ => new PlayerRecoveryDeadline(
                DateTimeOffset.UnixEpoch));
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
            if (existing.Ownership.IsValid &&
                !IsCurrentAccountSession(
                    existing.AccountId,
                    session,
                    existing.Ownership))
            {
                return false;
            }

            if (existing.WorldReady)
            {
                return true;
            }

            if (TryGetWorldInstance(
                    existing,
                    out var worldInstance))
            {
                unseenPlayers = InvokeWorldOwner(
                    worldInstance,
                    map => map.Snapshot()
                        .Where(candidate =>
                            candidate.WorldReady &&
                            !ReferenceEquals(
                                candidate.Session,
                                session) &&
                            (!knownWorldRevisions.TryGetValue(
                                 candidate.ObjectId,
                                 out var knownRevision) ||
                             knownRevision !=
                                 candidate.WorldRevision))
                        .ToArray());
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

}
