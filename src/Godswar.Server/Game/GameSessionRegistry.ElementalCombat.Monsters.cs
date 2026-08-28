using System.Collections.Concurrent;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static readonly ElementalEquipmentProfile
        EmptyPveElementalTargetProfile =
            ElementalAttributeCatalog.CalculateEquippedProfile([]);

    private readonly object _pveElementalCommitGate = new();
    private readonly ConcurrentDictionary<
        PveMonsterElementalKey,
        PveMonsterElementalState> _pveMonsterElementalStates = [];

    private PveElementalCommitResult CommitPveElementalHitsLocked(
        GameSessionContext source,
        ElementalCombatSessionState sourceState,
        ElementalEquipmentProfile sourceProfile,
        int sourceMaximumHealth,
        int sourceMaximumMana,
        CombatEventProvenance provenance,
        IReadOnlyList<PveElementalCommittedHit> committedHits,
        IReadOnlyList<MonsterRuntimeSnapshot> monsterSnapshot,
        IReadOnlySet<(uint ObjectId, uint SpawnGeneration)>
            medusaOwnedMonsters,
        DateTimeOffset committedAt)
    {
        var applications = new List<ElementalEffectApplication>();
        var plannedDamage = new List<ResonanceDamageIntent>();
        var plannedControls = new List<PveMonsterControlPlan>();
        var killCredits = new List<PveMonsterKillCredit>();
        var sourceHealthRecovery = 0L;
        foreach (var hit in committedHits
                     .Where(static value =>
                         value.CombatEventId != 0 &&
                         value.DamageResult.BeforeHealth >
                            value.DamageResult.AfterHealth)
                     .OrderBy(static value => value.TargetOrder)
                     .ThenBy(static value =>
                         value.DamageResult.ObjectId))
        {
            var damage = hit.DamageResult;
            if (damage.Monster.Definition.MapId != source.MapId ||
                damage.Monster.RuntimeInstanceId == Guid.Empty ||
                damage.Monster.SpawnGeneration == 0)
            {
                continue;
            }

            var combatEvent = new DeterministicCombatEventContext(
                hit.CombatEventId,
                source.MapId,
                source.CharacterId,
                damage.ObjectId,
                committedAt.ToUnixTimeMilliseconds(),
                provenance,
                Committed: true,
                IsPvp: false,
                default);
            if (!combatEvent.IsCommittedDirectHit)
            {
                continue;
            }

            // Bound Medusa damage currently has no typed atomic handoff for
            // periodic/status/resonance monster mutations. Suppress those
            // secondary effects before the commit policy can mutate either
            // the target status ledger or source resonance ledger.
            if (medusaOwnedMonsters.Contains((
                    damage.ObjectId,
                    damage.Monster.SpawnGeneration)))
            {
                continue;
            }

            var targetState = GetPveMonsterElementalState(
                source.WorldInstanceId,
                source.MapId,
                damage.Monster);
            var monsterCombatProfile = _gameplayCatalogs
                .MonsterCombatProfiles
                .Resolve(damage.Monster.Definition);
            var appliedDirectDamage = checked((long)(
                damage.BeforeHealth - damage.AfterHealth));
            lock (targetState.Gate)
            {
                ElementKind? authoredElement =
                    AuthoredElementalCombatV1.TrySelectDirectHitElement(
                        sourceProfile,
                        out var selectedElement)
                        ? selectedElement
                        : null;
                var committed = ElementalDirectHitCommitPolicy.Commit(
                    combatEvent,
                    sourceProfile,
                    sourceState.Resonance,
                    EmptyPveElementalTargetProfile,
                    targetState.Statuses,
                    authoredElement,
                    AuthoredElementalCombatV1.EffectTuning,
                    appliedDirectDamage,
                    sourceMaximumHealth,
                    primaryTargetIsBoss: monsterCombatProfile.IsBoss,
                    BuildPveResonanceCandidates(
                        source.MapId,
                        damage.Monster,
                        monsterSnapshot));
                if (committed is
                    {
                        ElementalApplicationAccepted: true,
                        ElementalApplication: { } application
                    })
                {
                    applications.Add(application);
                    if (application.Effect == ElementalEffectKind.Shock &&
                        !monsterCombatProfile.IsBoss &&
                        damage.AfterHealth > 0)
                    {
                        plannedControls.Add(new(
                            damage.ObjectId,
                            damage.Monster.SpawnGeneration,
                            application.DurationMilliseconds));
                    }
                }

                var resonance = committed.Resonance;
                plannedDamage.AddRange(resonance.DamageIntents ?? []);
                sourceHealthRecovery = checked(
                    sourceHealthRecovery +
                    resonance.SourceHealthRecovery);
                plannedControls.AddRange(
                    (resonance.ControlIntents ?? [])
                    .Where(value =>
                        value.TargetId == damage.ObjectId &&
                        value.StunMilliseconds > 0)
                    .Select(value => new PveMonsterControlPlan(
                        damage.ObjectId,
                        damage.Monster.SpawnGeneration,
                        value.StunMilliseconds)));
            }

            if (damage.Killed)
            {
                killCredits.Add(new(
                    hit.CombatEventId,
                    damage.ObjectId,
                    damage.Monster.SpawnGeneration));
                ClearPveMonsterElementalState(
                    source.WorldInstanceId,
                    damage.Monster);
            }
        }

        var damageCommits = ApplyPveElementalDamageIntentsLocked(
            source,
            plannedDamage,
            killCredits,
            committedAt);
        var controls = ApplyPveElementalControlsLocked(
            source,
            plannedControls,
            committedAt);
        var recovery = ApplyPveElementalSourceRecoveryLocked(
            source,
            sourceState,
            sourceProfile,
            sourceMaximumHealth,
            sourceMaximumMana,
            killCredits,
            sourceHealthRecovery,
            committedAt);
        return new(
            applications.AsReadOnly(),
            damageCommits,
            controls,
            recovery);
    }

    private IReadOnlyList<PveElementalDamageCommit>
        ApplyPveElementalDamageIntentsLocked(
            GameSessionContext source,
            IEnumerable<ResonanceDamageIntent> intents,
            ICollection<PveMonsterKillCredit> killCredits,
            DateTimeOffset committedAt)
    {
        var commits = new List<PveElementalDamageCommit>();
        foreach (var intent in intents)
        {
            if (intent.CanTriggerSecondaryCombatEffects ||
                intent.SourceCharacterId != source.CharacterId ||
                intent.TargetId is <= 0 or > uint.MaxValue ||
                intent.Damage <= 0 ||
                !TryGetPveElementalMonsterSnapshot(
                    source,
                    checked((uint)intent.TargetId),
                    out var target) ||
                !target.IsSpawned ||
                !target.IsAlive)
            {
                continue;
            }

            var requested = checked((uint)Math.Clamp(
                intent.Damage,
                1,
                uint.MaxValue));
            if (!TryApplyPveElementalMonsterDamage(
                    source,
                    target.ObjectId,
                    requested,
                    source.CharacterId,
                    target.SpawnGeneration,
                    target.HealthRevision,
                    committedAt,
                    out var damage) ||
                damage.BeforeHealth == damage.AfterHealth)
            {
                continue;
            }

            commits.Add(new(intent.Kind, intent.SourceEventId, damage));
            if (damage.Killed)
            {
                killCredits.Add(new(
                    intent.SourceEventId,
                    damage.ObjectId,
                    damage.Monster.SpawnGeneration));
                ClearPveMonsterElementalState(
                    source.WorldInstanceId,
                    damage.Monster);
            }
        }

        return commits.AsReadOnly();
    }

    private IReadOnlyList<MonsterStunResult>
        ApplyPveElementalControlsLocked(
            GameSessionContext source,
            IEnumerable<PveMonsterControlPlan> controls,
            DateTimeOffset committedAt)
    {
        var commits = new List<MonsterStunResult>();
        foreach (var control in controls
                     .GroupBy(static value => (
                         value.ObjectId,
                         value.SpawnGeneration))
                     .Select(static values => new PveMonsterControlPlan(
                         values.Key.ObjectId,
                         values.Key.SpawnGeneration,
                         values.Max(static value =>
                             value.DurationMilliseconds))))
        {
            if (TryApplyPveElementalMonsterStun(
                    source,
                    control.ObjectId,
                    TimeSpan.FromMilliseconds(
                        control.DurationMilliseconds),
                    control.SpawnGeneration,
                    committedAt,
                    out var result) &&
                result.Applied)
            {
                commits.Add(result);
            }
        }

        return commits.AsReadOnly();
    }

    private PveElementalSourceRecoveryCommit
        ApplyPveElementalSourceRecoveryLocked(
            GameSessionContext source,
            ElementalCombatSessionState sourceState,
            ElementalEquipmentProfile sourceProfile,
            int sourceMaximumHealth,
            int sourceMaximumMana,
            IEnumerable<PveMonsterKillCredit> killCredits,
            long sourceHealthRecovery,
            DateTimeOffset committedAt)
    {
        var character = source.Character;
        var beforeHealth = character.CurrentHp;
        var beforeMana = character.CurrentMp;
        var beforeRevision = character.VitalsRevision;
        if (beforeHealth <= 0)
        {
            return new(
                beforeHealth,
                beforeHealth,
                beforeMana,
                beforeMana,
                beforeRevision,
                beforeRevision);
        }

        var nextHealth = beforeHealth;
        var nextMana = beforeMana;
        sourceHealthRecovery = sourceState.Statuses.ApplyAdjustments(
            committedAt.ToUnixTimeMilliseconds(),
            movementSpeed: 0,
            physicalDefense: 0,
            magicDefense: 0,
            hitRating: 0,
            healingReceived: sourceHealthRecovery).HealingReceived;
        nextHealth = checked((int)Math.Min(
            sourceMaximumHealth,
            nextHealth + sourceHealthRecovery));
        var killOrdinal = 0;
        foreach (var credit in killCredits
                     .DistinctBy(static value => (
                         value.ObjectId,
                         value.SpawnGeneration))
                     .OrderBy(static value => value.ObjectId))
        {
            var killEvent = AuthoredElementalCombatV1
                .CreditedPveKillEvent(
                    credit.SourceEventId,
                    source.CharacterId,
                    credit.ObjectId,
                    source.MapId,
                    killOrdinal: checked(++killOrdinal),
                    committedAt);
            var restored = ElementalResonanceExecutionPolicy
                .ProcessCreditedKill(
                    killEvent,
                    sourceProfile,
                    sourceState.Resonance,
                    nextHealth,
                    nextMana,
                    sourceMaximumHealth,
                    sourceMaximumMana);
            var restoredHealth = sourceState.Statuses.ApplyAdjustments(
                committedAt.ToUnixTimeMilliseconds(),
                movementSpeed: 0,
                physicalDefense: 0,
                magicDefense: 0,
                hitRating: 0,
                healingReceived: restored.AppliedHealth).HealingReceived;
            nextHealth = checked((int)Math.Min(
                sourceMaximumHealth,
                nextHealth + restoredHealth));
            nextMana = checked((int)Math.Min(
                sourceMaximumMana,
                nextMana + restored.AppliedMana));
        }
        if (nextHealth != beforeHealth || nextMana != beforeMana)
        {
            character.CurrentHp = nextHealth;
            character.CurrentMp = nextMana;
            character.MarkVitalsChanged();
        }

        return new(
            beforeHealth,
            character.CurrentHp,
            beforeMana,
            character.CurrentMp,
            beforeRevision,
            character.VitalsRevision);
    }

    private PveMonsterElementalState GetPveMonsterElementalState(
        WorldInstanceId worldInstanceId,
        byte mapId,
        MonsterRuntimeSnapshot monster)
    {
        var key = new PveMonsterElementalKey(
            worldInstanceId,
            monster.ObjectId);
        var identity = new PveMonsterElementalIdentity(
            mapId,
            monster.SpawnGeneration,
            monster.RuntimeInstanceId);
        return _pveMonsterElementalStates.AddOrUpdate(
            key,
            _ => new(monster.ObjectId, identity),
            (_, existing) => existing.Identity == identity
                ? existing
                : new(monster.ObjectId, identity));
    }

    private void ClearPveMonsterElementalState(
        WorldInstanceId worldInstanceId,
        MonsterRuntimeSnapshot monster)
    {
        var key = new PveMonsterElementalKey(
            worldInstanceId,
            monster.ObjectId);
        if (_pveMonsterElementalStates.TryGetValue(key, out var state) &&
            state.Identity.SpawnGeneration == monster.SpawnGeneration &&
            state.Identity.RuntimeInstanceId == monster.RuntimeInstanceId)
        {
            lock (state.Gate)
            {
                state.Statuses.ClearOnDeath();
            }
        }
    }

    private IReadOnlyList<ResonanceTargetCandidate>
        BuildPveResonanceCandidates(
            byte mapId,
            MonsterRuntimeSnapshot primary,
            IEnumerable<MonsterRuntimeSnapshot> candidates) =>
        candidates
            .Where(value =>
                value.ObjectId != primary.ObjectId &&
                value.IsSpawned &&
                value.IsAlive &&
                value.Definition.MapId == mapId)
            .Select(value => new ResonanceTargetCandidate(
                value.ObjectId,
                mapId,
                AuthoredElementalCombatV1.AcceptedDistanceMillimeters(
                    primary.X,
                    primary.Z,
                    value.X,
                    value.Z),
                IsAlive: true,
                IsBoss: _gameplayCatalogs.MonsterCombatProfiles
                    .Resolve(value.Definition).IsBoss,
                ResonanceTargetAuthority.AuthoritativeMonster,
                default))
            .OrderBy(static value => value.DistanceMillimeters)
            .ThenBy(static value => value.TargetId)
            .ToArray();

    private readonly record struct PveMonsterElementalKey(
        WorldInstanceId WorldInstanceId,
        uint ObjectId);

    private readonly record struct PveMonsterElementalIdentity(
        byte MapId,
        uint SpawnGeneration,
        Guid RuntimeInstanceId);

    private sealed class PveMonsterElementalState(
        uint objectId,
        PveMonsterElementalIdentity identity)
    {
        public PveMonsterElementalIdentity Identity { get; } = identity;
        public object Gate { get; } = new();
        public ElementalStatusState Statuses { get; } = new(objectId);
    }

    private readonly record struct PveMonsterControlPlan(
        uint ObjectId,
        uint SpawnGeneration,
        int DurationMilliseconds);

    private readonly record struct PveMonsterKillCredit(
        ulong SourceEventId,
        uint ObjectId,
        uint SpawnGeneration);
}
