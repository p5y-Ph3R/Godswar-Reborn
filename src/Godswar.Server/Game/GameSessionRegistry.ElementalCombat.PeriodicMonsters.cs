using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private IReadOnlyList<MonsterElementalBurnCommit>
        CommitDueMonsterElementalBurns(DateTimeOffset now)
    {
        var applied = new List<MonsterBurnMutation>();
        lock (_pveElementalCommitGate)
        {
            foreach (var pair in _pveMonsterElementalStates.ToArray())
            {
                var key = pair.Key;
                var state = pair.Value;
                if (!WorldInstances.TryFind(
                        key.WorldInstanceId,
                        out var runtime) ||
                    runtime.MapId != state.Identity.MapId)
                {
                    ClearStaleMonsterElementalState(key, state);
                    continue;
                }

                lock (state.Gate)
                {
                    if (!TryGetMonsterSnapshotInRuntime(
                            runtime,
                            key.ObjectId,
                            out var current) ||
                        !MatchesMonsterElementalIdentity(state, current) ||
                        !current.IsSpawned ||
                        !current.IsAlive)
                    {
                        state.Statuses.ClearOnDeath();
                        _pveMonsterElementalStates.TryRemove(key, out _);
                        continue;
                    }

                    var intents = state.Statuses.CollectDuePeriodicDamage(
                        now.ToUnixTimeMilliseconds());
                    long totalDamage = 0;
                    var remainingHealth = (long)current.CurrentHealth;
                    ElementalPeriodicDamageIntent terminalIntent = default;
                    var intentsValid = true;
                    foreach (var intent in intents)
                    {
                        if (!IsValidMonsterBurnIntent(key, state, intent))
                        {
                            intentsValid = false;
                            break;
                        }

                        var appliedDamage = Math.Min(
                            remainingHealth,
                            intent.Damage);
                        totalDamage = checked(totalDamage + appliedDamage);
                        remainingHealth -= appliedDamage;
                        terminalIntent = intent;
                        if (remainingHealth == 0)
                        {
                            break;
                        }
                    }

                    if (!intentsValid ||
                        totalDamage <= 0 ||
                        terminalIntent.TickOrdinal <= 0)
                    {
                        if (!intentsValid)
                        {
                            state.Statuses.ClearOnReconnect();
                            _pveMonsterElementalStates.TryRemove(key, out _);
                        }

                        continue;
                    }

                    // All overdue authored ticks are one target-owner mutation.
                    // This preserves every tick's damage while advancing the
                    // health ledger exactly once under loop lag.
                    var aggregateIntent = terminalIntent with
                    {
                        Damage = totalDamage
                    };
                    if (!TryApplyMonsterBurnInRuntime(
                            runtime,
                            state.Identity,
                            key.ObjectId,
                            aggregateIntent,
                            now,
                            out var damage))
                    {
                        // Collection already advanced the status tick ledger.
                        // A failed guard is terminal for this stale identity.
                        state.Statuses.ClearOnReconnect();
                        _pveMonsterElementalStates.TryRemove(key, out _);
                        continue;
                    }

                    var damageIntent = new ResonanceDamageIntent(
                            ResonanceDamageKind.ElementalBurnTick,
                            terminalIntent.SourceCharacterId,
                            terminalIntent.TargetCharacterId,
                            terminalIntent.SourceEventId,
                            checked((long)(
                                damage.BeforeHealth - damage.AfterHealth)),
                            CombatEventProvenance.ElementalStatus);
                    applied.Add(new(
                        key.WorldInstanceId,
                        terminalIntent.TickOrdinal,
                        damageIntent,
                        damage));
                    if (damage.Killed)
                    {
                        state.Statuses.ClearOnDeath();
                        _pveMonsterElementalStates.TryRemove(key, out _);
                    }
                }
            }
        }

        var commits = new List<MonsterElementalBurnCommit>(applied.Count);
        foreach (var mutation in applied
                     .OrderBy(static value =>
                         value.WorldInstanceId.Value)
                     .ThenBy(static value =>
                         value.DamageResult.ObjectId)
                     .ThenBy(static value => value.TickOrdinal))
        {
            var source = FindCurrentMonsterBurnSource(mutation);
            var routing = source ?? GetWorldInstanceSessions(
                    mutation.WorldInstanceId)
                .OrderBy(static value => value.CharacterId)
                .FirstOrDefault();
            var recovery = mutation.DamageResult.Killed && source is not null
                ? ApplyMonsterBurnKillRecovery(
                    source,
                    mutation,
                    now)
                : default;
            commits.Add(new(
                mutation.WorldInstanceId,
                checked((int)mutation.Intent.SourceCharacterId),
                source,
                routing,
                mutation.Intent,
                mutation.DamageResult,
                recovery));
        }

        return commits.AsReadOnly();
    }

    private void ClearStaleMonsterElementalState(
        PveMonsterElementalKey key,
        PveMonsterElementalState state)
    {
        lock (state.Gate)
        {
            state.Statuses.ClearOnReconnect();
        }

        _pveMonsterElementalStates.TryRemove(key, out _);
    }

    private bool TryGetMonsterSnapshotInRuntime(
        WorldInstanceRuntime runtime,
        uint objectId,
        out MonsterRuntimeSnapshot snapshot)
    {
        var result = InvokeWorldOwner(
            runtime,
            map =>
            {
                var found = map.TryGetMonsterSnapshot(
                    objectId,
                    out var value);
                return (Found: found, Value: value);
            });
        snapshot = result.Value;
        return result.Found;
    }

    private bool TryApplyMonsterBurnInRuntime(
        WorldInstanceRuntime runtime,
        PveMonsterElementalIdentity identity,
        uint objectId,
        ElementalPeriodicDamageIntent intent,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        var requested = checked((uint)Math.Clamp(
            intent.Damage,
            1,
            uint.MaxValue));
        var attempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                MonsterDamageResult value = default!;
                var committed = map.TryGetMonsterSnapshot(
                        objectId,
                        out var current) &&
                    current.IsSpawned &&
                    current.IsAlive &&
                    identity.MapId == current.Definition.MapId &&
                    identity.SpawnGeneration == current.SpawnGeneration &&
                    identity.RuntimeInstanceId == current.RuntimeInstanceId &&
                    map.TryApplyMonsterPeriodicDamageGuarded(
                    objectId,
                    requested,
                    checked((int)intent.SourceCharacterId),
                    current.SpawnGeneration,
                    current.HealthRevision,
                    now,
                    out value);
                return (Committed: committed, Value: value);
            });
        result = attempt.Value;
        return attempt.Committed &&
            result.BeforeHealth > result.AfterHealth;
    }

    private static bool MatchesMonsterElementalIdentity(
        PveMonsterElementalState state,
        MonsterRuntimeSnapshot monster) =>
        state.Identity.MapId == monster.Definition.MapId &&
        state.Identity.SpawnGeneration == monster.SpawnGeneration &&
        state.Identity.RuntimeInstanceId == monster.RuntimeInstanceId;

    private static bool IsValidMonsterBurnIntent(
        PveMonsterElementalKey key,
        PveMonsterElementalState state,
        ElementalPeriodicDamageIntent intent) =>
        intent.Effect == ElementalEffectKind.Burn &&
        intent.Provenance == CombatEventProvenance.ElementalStatus &&
        intent.SourceCharacterId is > 0 and <= int.MaxValue &&
        intent.TargetCharacterId == key.ObjectId &&
        intent.TargetCharacterId == state.Statuses.OwnerCharacterId &&
        intent.SourceEventId != 0 &&
        intent.TickOrdinal > 0 &&
        intent.Damage > 0;

    private GameSessionContext? FindCurrentMonsterBurnSource(
        MonsterBurnMutation mutation) =>
        GetWorldInstanceSessions(mutation.WorldInstanceId)
            .FirstOrDefault(candidate =>
                candidate.CharacterId ==
                    mutation.Intent.SourceCharacterId &&
                candidate.MapId == mutation.DamageResult
                    .Monster.Definition.MapId);

    private PveElementalSourceRecoveryCommit
        ApplyMonsterBurnKillRecovery(
            GameSessionContext source,
            MonsterBurnMutation mutation,
            DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(source.Session, out var current) ||
                !ReferenceEquals(current, source) ||
                !current.WorldReady ||
                current.WorldInstanceId != mutation.WorldInstanceId ||
                current.MapId != mutation.DamageResult.Monster.Definition.MapId ||
                current.Character.CurrentHp <= 0 ||
                !IsCurrentAccountSession(
                    current.AccountId,
                    current.Session,
                    current.Ownership) ||
                !TryGetElementalCombatSession(
                    current.Session,
                    new ElementalCombatSessionFence(
                        current.CharacterId,
                        current.MapId,
                        current.Ownership),
                    out var state))
            {
                return default;
            }

            lock (current.Character.VitalsSync)
            lock (state.Gate)
            {
                return ApplyPveElementalSourceRecoveryLocked(
                    current,
                    state,
                    current.Character.ElementalEquipment,
                    current.Character.MaxHp,
                    current.Character.MaxMp,
                    [new PveMonsterKillCredit(
                        mutation.Intent.SourceEventId,
                        mutation.DamageResult.ObjectId,
                        mutation.DamageResult.Monster.SpawnGeneration)],
                    0,
                    now);
            }
        }
    }

    private readonly record struct MonsterBurnMutation(
        Godswar.Server.Domain.World.Instances.WorldInstanceId WorldInstanceId,
        int TickOrdinal,
        ResonanceDamageIntent Intent,
        MonsterDamageResult DamageResult);
}
