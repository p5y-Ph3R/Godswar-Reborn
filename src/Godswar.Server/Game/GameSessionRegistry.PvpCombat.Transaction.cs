using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private PvpBasicAttackDecision ResolveCommittedPvpHitLocked(
        GameSessionContext attacker,
        GameSessionContext target,
        PvpEligibilityResult eligibility,
        CombatResolution baseResolution,
        DeterministicCombatEventContext attemptEvent,
        CombatTargetStats targetCombat,
        IReadOnlyList<PvpElementalCandidate> candidates,
        DateTimeOffset now)
    {
        if (!baseResolution.Hit)
        {
            return AcceptedPvpDecision(
                eligibility,
                baseResolution,
                attacker,
                target,
                appliedDamage: 0,
                lifeHealing: 0,
                reboundDamage: 0,
                []);
        }

        var damageAdjustment = new PvpElementalDamageAdjustment(
            new(
                baseResolution.Damage,
                baseResolution.Damage,
                HadesExecuteApplied: false,
                AeolusMomentumPendingCommit: false),
            new(
                baseResolution.Damage,
                baseResolution.Damage,
                PreventedDamage: 0,
                target.Character.CurrentHp - baseResolution.Damage,
                Evaded: false,
                PoseidonGuardApplied: false,
                ApolloLethalProtectionApplied: false,
                ConsumedBarrier: 0,
                GuardHealthRecovery: 0,
                GuardManaRecovery: 0));
        if (TryAdjustPvpElementalDamageLocked(
                attacker,
                target,
                attemptEvent,
                baseResolution.Damage,
                out var elementalDamageAdjustment))
        {
            damageAdjustment = elementalDamageAdjustment;
        }
        if (damageAdjustment.Incoming.Evaded)
        {
            var evaded = baseResolution with
            {
                Outcome = CombatHitOutcome.Miss,
                Damage = 0
            };
            return AcceptedPvpDecision(
                eligibility,
                evaded,
                attacker,
                target,
                appliedDamage: 0,
                lifeHealing: 0,
                reboundDamage: 0,
                []);
        }

        var resolution = baseResolution with
        {
            Damage = checked((uint)Math.Clamp(
                damageAdjustment.Incoming.AdjustedDamage,
                0,
                uint.MaxValue))
        };
        var participants = candidates
            .Select(static value => value.Context)
            .Append(attacker)
            .Append(target)
            .DistinctBy(static value => value.CharacterId)
            .ToArray();
        var vitals = participants.ToDictionary(
            static value => value.CharacterId,
            static value => new MutablePvpVitals(value));
        var sourceVitals = vitals[attacker.CharacterId];
        var targetVitals = vitals[target.CharacterId];
        var directDamage = targetVitals.ApplyDamage(resolution.Damage);
        var secondary = CombatSecondaryEffectPolicy.Resolve(
            checked((uint)directDamage),
            CombatCharacterStatsAdapter.FromCharacter(attacker.Character),
            targetCombat);

        var committedEvent = attemptEvent with { Committed = true };
        var post = EmptyPvpElementalPostCommit();
        if (directDamage > 0)
        {
            _ = TryCommitPvpElementalHitLocked(
                attacker,
                target,
                committedEvent,
                directDamage,
                candidates.Select(static value => value.Candidate).ToArray(),
                out post);
        }

        var targetGuardHealth = targetVitals.CurrentHealth > 0
            ? AdjustPvpElementalHealingReceivedLocked(
                target,
                now,
                damageAdjustment.Incoming.GuardHealthRecovery)
            : 0;
        var appliedTargetGuardHealth = targetVitals.ApplyHealthRecovery(
            targetGuardHealth);
        var appliedTargetGuardMana = targetVitals.ApplyManaRecovery(
            damageAdjustment.Incoming.GuardManaRecovery);

        var requestedLifeHealing = AdjustPvpElementalHealingReceivedLocked(
            attacker,
            now,
            secondary.LifeAbsorptionHealing);
        var appliedLifeHealing = sourceVitals.ApplyHealthRecovery(
            requestedLifeHealing);
        var requestedElementalHealing =
            AdjustPvpElementalHealingReceivedLocked(
                attacker,
                now,
                post.SourceResonance.SourceHealthRecovery);
        var appliedElementalHealing = sourceVitals.ApplyHealthRecovery(
            requestedElementalHealing);
        var reboundDamage = sourceVitals.ApplyDamage(
            secondary.ReboundDamage);

        var damageCommits = new List<PvpElementalDamageCommit>();
        var killCredits = new List<PvpKillCredit>();
        if (targetVitals.WasKilledBy(directDamage))
        {
            killCredits.Add(new(attacker, target, eligibility));
        }
        if (sourceVitals.WasKilledBy(reboundDamage))
        {
            killCredits.Add(new(
                target,
                attacker,
                _gameplayCatalogs.PvpWorldAuthority
                    .EvaluateOpposingFaction(
                        target.Character,
                        attacker.Character,
                        now)));
        }

        var contexts = participants.ToDictionary(
            static value => (long)value.CharacterId);
        var admissions = candidates.ToDictionary(
            static value => (long)value.Context.CharacterId,
            static value => value.Candidate.PvpAdmission);
        admissions[target.CharacterId] = eligibility;
        ApplyPvpElementalDamageIntents(
            post.SourceResonance.DamageIntents ?? [],
            contexts,
            admissions,
            vitals,
            damageCommits,
            killCredits,
            now);
        if (post.Reflection is { } reflection)
        {
            ApplyPvpElementalDamageIntents(
                [reflection],
                contexts,
                admissions,
                vitals,
                damageCommits,
                killCredits,
                now);
        }

        var killHealthRecovery = 0L;
        var killManaRecovery = 0L;
        var killOrdinal = 0;
        foreach (var credit in killCredits
                     .DistinctBy(static value =>
                         (value.Source.CharacterId,
                          value.Target.CharacterId)))
        {
            var creditSource = vitals[credit.Source.CharacterId];
            if (creditSource.CurrentHealth <= 0 ||
                !credit.Eligibility.Allowed)
            {
                continue;
            }

            killOrdinal++;
            var killEvent = AuthoredElementalCombatV1.CreditedKillEvent(
                committedEvent.EventId,
                credit.Source.CharacterId,
                credit.Target.CharacterId,
                credit.Source.MapId,
                killOrdinal,
                now,
                credit.Eligibility);
            var fence = new ElementalCombatSessionFence(
                credit.Source.CharacterId,
                credit.Source.MapId,
                credit.Source.Ownership);
            if (!TryProcessElementalCreditedKill(
                    credit.Source.Session,
                    fence,
                    killEvent,
                    credit.Source.Character.ElementalEquipment,
                    creditSource.CurrentHealth,
                    creditSource.CurrentMana,
                    credit.Source.Character.MaxHp,
                    credit.Source.Character.MaxMp,
                    out var restored))
            {
                continue;
            }

            var health = AdjustPvpElementalHealingReceivedLocked(
                credit.Source,
                now,
                restored.AppliedHealth);
            killHealthRecovery = checked(killHealthRecovery +
                creditSource.ApplyHealthRecovery(health));
            killManaRecovery = checked(killManaRecovery +
                creditSource.ApplyManaRecovery(restored.AppliedMana));
        }

        var changed = new List<GameSessionContext>();
        foreach (var value in vitals.Values.OrderBy(
                     static value => value.Context.CharacterId))
        {
            if (value.Commit())
            {
                changed.Add(value.Context);
            }
        }

        var killed = vitals.Values
            .Where(static value => value.CurrentHealth <= 0)
            .Select(static value => value.Context)
            .OrderBy(static value => value.CharacterId)
            .ToArray();
        var controls = post.ResonanceStunApplied &&
            targetVitals.CurrentHealth > 0
            ? (post.SourceResonance.ControlIntents ?? [])
                .Where(value => value.TargetId == target.CharacterId)
                .Select(value => new PvpElementalControlCommit(
                    target,
                    value.StunMilliseconds))
                .ToArray()
            : [];
        var lifeHealing = checked((uint)Math.Clamp(
            appliedLifeHealing,
            0,
            uint.MaxValue));
        return AcceptedPvpDecision(
            eligibility,
            resolution,
            attacker,
            target,
            checked((uint)directDamage),
            lifeHealing,
            checked((uint)reboundDamage),
            changed) with
        {
            ElementalApplications = post.Applications ?? [],
            ElementalDamageCommits = damageCommits.AsReadOnly(),
            ElementalControlCommits = controls,
            KilledPlayers = killed,
            ElementalHealthRecovery = checked(
                appliedElementalHealing +
                appliedTargetGuardHealth +
                killHealthRecovery),
            ElementalManaRecovery = checked(
                appliedTargetGuardMana + killManaRecovery)
        };
    }

    private static PvpBasicAttackDecision AcceptedPvpDecision(
        PvpEligibilityResult eligibility,
        CombatResolution resolution,
        GameSessionContext attacker,
        GameSessionContext target,
        uint appliedDamage,
        uint lifeHealing,
        uint reboundDamage,
        IReadOnlyList<GameSessionContext> changed) =>
        new(
            true,
            PvpBasicAttackRejectionReason.None,
            eligibility,
            resolution,
            attacker,
            target,
            appliedDamage,
            lifeHealing,
            reboundDamage,
            target.Character.CurrentHp <= 0,
            attacker.Character.CurrentHp <= 0,
            attacker.Character.CurrentHp,
            target.Character.CurrentHp)
        {
            ChangedVitals = changed,
            KilledPlayers = new[] { attacker, target }
                .Where(static value => value.Character.CurrentHp <= 0)
                .ToArray()
        };

    private void ApplyPvpElementalDamageIntents(
        IEnumerable<ResonanceDamageIntent> intents,
        IReadOnlyDictionary<long, GameSessionContext> contexts,
        IReadOnlyDictionary<long, PvpEligibilityResult> admissions,
        IReadOnlyDictionary<int, MutablePvpVitals> vitals,
        ICollection<PvpElementalDamageCommit> commits,
        ICollection<PvpKillCredit> killCredits,
        DateTimeOffset now)
    {
        foreach (var intent in intents)
        {
            if (intent.CanTriggerSecondaryCombatEffects ||
                !contexts.TryGetValue(intent.SourceCharacterId, out var source) ||
                !contexts.TryGetValue(intent.TargetId, out var target) ||
                !vitals.TryGetValue(target.CharacterId, out var targetVitals) ||
                targetVitals.CurrentHealth <= 0)
            {
                continue;
            }

            var applied = targetVitals.ApplyDamage(intent.Damage);
            if (applied <= 0)
            {
                continue;
            }

            var killed = targetVitals.WasKilledBy(applied);
            commits.Add(new(
                intent.Kind,
                source,
                target,
                intent.SourceEventId,
                checked((int)applied),
                targetVitals.CurrentHealth,
                killed));
            if (killed)
            {
                var admission = admissions.TryGetValue(
                    target.CharacterId,
                    out var value)
                    ? value
                    : _gameplayCatalogs.PvpWorldAuthority
                        .EvaluateOpposingFaction(
                            source.Character,
                            target.Character,
                            now);
                killCredits.Add(new(source, target, admission));
            }
        }
    }

    private static PvpElementalPostCommit EmptyPvpElementalPostCommit() =>
        new(
            [],
            new([], [], 0, 0, false, false, 0),
            Reflection: null,
            ResonanceStunApplied: false);

    private long AdjustPvpElementalHealingReceivedLocked(
        GameSessionContext context,
        DateTimeOffset now,
        long requested)
    {
        if (requested <= 0)
        {
            return 0;
        }

        var fence = new ElementalCombatSessionFence(
            context.CharacterId,
            context.MapId,
            context.Ownership);
        return TryGetElementalStatusAdjustment(
            context.Session,
            fence,
            now.ToUnixTimeMilliseconds(),
            movementSpeed: 0,
            physicalDefense: 0,
            magicDefense: 0,
            hitRating: 0,
            healingReceived: requested,
            out var status)
            ? status.HealingReceived
            : requested;
    }

    private static IDisposable AcquirePvpVitalsLocks(
        IEnumerable<GameSessionContext> contexts) =>
        new PvpVitalsLockScope(contexts);

    private readonly record struct PvpElementalCandidate(
        GameSessionContext Context,
        ResonanceTargetCandidate Candidate);

    private readonly record struct PvpKillCredit(
        GameSessionContext Source,
        GameSessionContext Target,
        PvpEligibilityResult Eligibility);

    private sealed class MutablePvpVitals
    {
        public MutablePvpVitals(GameSessionContext context)
        {
            Context = context;
            InitialHealth = context.Character.CurrentHp;
            InitialMana = context.Character.CurrentMp;
            CurrentHealth = InitialHealth;
            CurrentMana = InitialMana;
        }

        public GameSessionContext Context { get; }
        public int InitialHealth { get; }
        public int InitialMana { get; }
        public int CurrentHealth { get; private set; }
        public int CurrentMana { get; private set; }

        public long ApplyDamage(long requested)
        {
            var applied = Math.Min(
                Math.Max(0, requested),
                Math.Max(0, CurrentHealth));
            CurrentHealth = checked(CurrentHealth - (int)applied);
            return applied;
        }

        public long ApplyHealthRecovery(long requested)
        {
            if (requested <= 0 || CurrentHealth <= 0)
            {
                return 0;
            }

            var applied = Math.Min(
                requested,
                Math.Max(0, Context.Character.MaxHp - CurrentHealth));
            CurrentHealth = checked(CurrentHealth + (int)applied);
            return applied;
        }

        public long ApplyManaRecovery(long requested)
        {
            if (requested <= 0 || CurrentHealth <= 0)
            {
                return 0;
            }

            var applied = Math.Min(
                requested,
                Math.Max(0, Context.Character.MaxMp - CurrentMana));
            CurrentMana = checked(CurrentMana + (int)applied);
            return applied;
        }

        public bool WasKilledBy(long appliedDamage) =>
            appliedDamage > 0 && CurrentHealth == 0;

        public bool Commit()
        {
            if (CurrentHealth == InitialHealth && CurrentMana == InitialMana)
            {
                return false;
            }

            Context.Character.CurrentHp = CurrentHealth;
            Context.Character.CurrentMp = CurrentMana;
            Context.Character.MarkVitalsChanged();
            return true;
        }
    }

    private sealed class PvpVitalsLockScope : IDisposable
    {
        private readonly object[] _locks;

        public PvpVitalsLockScope(IEnumerable<GameSessionContext> contexts)
        {
            _locks = contexts
                .DistinctBy(static value => value.CharacterId)
                .OrderBy(static value => value.CharacterId)
                .Select(static value => value.Character.VitalsSync)
                .ToArray();
            var entered = 0;
            try
            {
                for (; entered < _locks.Length; entered++)
                {
                    Monitor.Enter(_locks[entered]);
                }
            }
            catch
            {
                for (var index = entered - 1; index >= 0; index--)
                {
                    Monitor.Exit(_locks[index]);
                }

                throw;
            }
        }

        public void Dispose()
        {
            for (var index = _locks.Length - 1; index >= 0; index--)
            {
                Monitor.Exit(_locks[index]);
            }
        }
    }
}
