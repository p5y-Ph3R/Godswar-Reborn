using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalResonanceContractChecks
{
    private static void CheckDeterministicExecution()
    {
        CheckFireExecution();
        CheckWaterExecution();
        CheckLightningExecution();
        CheckEarthExecution();
        CheckWindExecution();
        CheckLightExecution();
        CheckDarkExecution();
    }

    private static void CheckFireExecution()
    {
        var profile = ResonanceProfile(ElementKind.Fire, 10);
        var source = new ElementalResonanceState(10);
        var target = new ElementalStatusState(20);
        ResonancePostCommitResult result = default;
        for (ulong sequence = 1; sequence <= 5; sequence++)
        {
            result = ElementalResonanceExecutionPolicy.ProcessCommittedDirectHit(
                Direct(sequence),
                profile,
                source,
                target,
                appliedDirectDamage: 10_000,
                sourceMaximumHealth: 10_000,
                primaryTargetIsBoss: false,
                additionalTargets: []);
        }

        Check.True(
            result.BurnApplied &&
            result.BurnDetonated &&
            result.DetonatedBurnDamage == 2_200 &&
            result.DamageIntents.Count == 1 &&
            result.DamageIntents[0].Kind ==
                ResonanceDamageKind.PrometheusDetonation &&
            !result.DamageIntents[0].CanTriggerSecondaryCombatEffects,
            "Prometheus replaces its Burn, detonates on hit five, and emits a terminal proc");
        var replay = ElementalResonanceExecutionPolicy.ProcessCommittedDirectHit(
            Direct(5), profile, source, target, 10_000, 10_000, false, []);
        Check.True(
            replay.DamageIntents.Count == 0 && !replay.BurnApplied,
            "Prometheus commit replay is idempotently rejected");
    }

    private static void CheckWaterExecution()
    {
        var profile = ResonanceProfile(ElementKind.Water, 10);
        var state = new ElementalResonanceState(20);
        IncomingResonanceAdjustment incoming = default;
        for (ulong sequence = 1; sequence <= 5; sequence++)
        {
            incoming = ElementalResonanceExecutionPolicy
                .AdjustIncomingDirectDamage(
                    Direct(sequence),
                    profile,
                    state,
                    1_000,
                    10_000,
                    10_000,
                    1_000);
        }

        Check.True(
            incoming.PoseidonGuardApplied &&
            incoming.AdjustedDamage == 750 &&
            incoming.GuardHealthRecovery == 125 &&
            incoming.GuardManaRecovery == 30,
            "Poseidon guards hit five and caps prevented-damage recovery per resource");

        var pulseState = new ElementalResonanceState(20);
        var first = ElementalResonanceExecutionPolicy.ProcessPeriodicRecovery(
            RecoveryEvent(100, 0),
            profile,
            pulseState,
            9_000,
            900,
            10_000,
            1_000);
        var due = ElementalResonanceExecutionPolicy.ProcessPeriodicRecovery(
            RecoveryEvent(101, 6_000),
            profile,
            pulseState,
            9_000,
            900,
            10_000,
            1_000);
        Check.True(
            first.AppliedHealth == 0 &&
            due.AppliedHealth == 100 &&
            due.AppliedMana == 10,
            "Poseidon emits one bounded pulse after its six-second interval");
    }

    private static void CheckLightningExecution()
    {
        var profile = ResonanceProfile(ElementKind.Lightning, 10);
        var state = new ElementalResonanceState(10);
        var statuses = new ElementalStatusState(20);
        var candidates = new[]
        {
            MonsterCandidate(40, 2_000),
            new ResonanceTargetCandidate(
                50, 7, 500, true, false,
                ResonanceTargetAuthority.AdmittedPlayer, default),
            MonsterCandidate(30, 1_000),
            MonsterCandidate(60, 1_000) with { MapId = 8 }
        };
        ResonancePostCommitResult fourth = default;
        for (ulong sequence = 1; sequence <= 4; sequence++)
        {
            fourth = ElementalResonanceExecutionPolicy.ProcessCommittedDirectHit(
                Direct(sequence),
                profile,
                state,
                statuses,
                1_000,
                10_000,
                false,
                candidates);
        }

        Check.True(
            fourth.DamageIntents.Count == 3 &&
            fourth.DamageIntents.Any(value =>
                value.Kind == ResonanceDamageKind.ZeusBolt &&
                value.TargetId == 20 && value.Damage == 150) &&
            fourth.DamageIntents.Any(value =>
                value.Kind == ResonanceDamageKind.ZeusChain &&
                value.TargetId == 30 && value.Damage == 100) &&
            fourth.DamageIntents.Any(value =>
                value.Kind == ResonanceDamageKind.ZeusStormCrown &&
                value.TargetId == 40 && value.Damage == 50) &&
            fourth.ControlIntents.Count == 1 &&
            fourth.ControlIntents[0].StunMilliseconds == 1_000,
            "Zeus orders admitted targets deterministically and preserves bolt/chain/crown values");
    }

    private static void CheckEarthExecution()
    {
        var profile = ResonanceProfile(ElementKind.Earth, 10);
        var passive = ElementalResonanceExecutionPolicy.ApplyPassiveBonuses(
            profile, 10_000, 1_000);
        var state = new ElementalResonanceState(20);
        var combatEvent = Direct(1);
        var incoming = ElementalResonanceExecutionPolicy
            .AdjustIncomingDirectDamage(
                combatEvent,
                profile,
                state,
                1_000,
                10_000,
                10_000,
                1_000);
        var reflection = ElementalResonanceExecutionPolicy
            .PlanCommittedReflection(
                combatEvent,
                profile,
                state,
                incoming.AdjustedDamage,
                10_000);
        Check.True(
            passive.MaximumHealth == 10_800 &&
            incoming.AdjustedDamage == 920 &&
            reflection.HasValue &&
            reflection.Value.Damage == 138 &&
            reflection.Value.Provenance == CombatEventProvenance.Reflection &&
            !reflection.Value.CanTriggerSecondaryCombatEffects,
            "Gaia applies max-HP, final mitigation, and capped non-recursive reflection");
    }

    private static void CheckWindExecution()
    {
        var profile = ResonanceProfile(ElementKind.Wind, 10);
        var passive = ElementalResonanceExecutionPolicy.ApplyPassiveBonuses(
            profile, 10_000, 1_000);
        var source = new ElementalResonanceState(10);
        var movement = ElementalResonanceExecutionPolicy.ProcessAcceptedMovement(
            MovementEvent(1, 0), profile, source, 5_000);
        var pre = ElementalResonanceExecutionPolicy.AdjustOutgoingDirectDamage(
            Direct(2, time: 1_000), profile, source, 1_000, 10_000, 10_000);
        _ = ElementalResonanceExecutionPolicy.ProcessCommittedDirectHit(
            Direct(2, time: 1_000),
            profile,
            source,
            new ElementalStatusState(20),
            1_100,
            10_000,
            false,
            []);

        var target = new ElementalResonanceState(20);
        IncomingResonanceAdjustment sixth = default;
        for (ulong sequence = 1; sequence <= 6; sequence++)
        {
            sixth = ElementalResonanceExecutionPolicy.AdjustIncomingDirectDamage(
                Direct(sequence), profile, target, 1_000, 10_000, 10_000, 1_000);
        }

        Check.True(
            passive.MovementSpeed == 1_050 &&
            movement.MomentumReady &&
            pre.AeolusMomentumPendingCommit &&
            pre.AdjustedDamage == 1_100 &&
            !source.HasMomentum(1_001) &&
            sixth.Evaded && sixth.AdjustedDamage == 0,
            "Aeolus applies speed, committed Momentum consumption, and sixth-hit evasion");
    }

    private static void CheckLightExecution()
    {
        var profile = ResonanceProfile(ElementKind.Light, 10);
        var state = new ElementalResonanceState(20);
        var recovery = ElementalResonanceExecutionPolicy.ProcessRecoveryPulse(
            RecoveryEvent(1, 0),
            profile,
            state,
            requestedHealth: 200,
            requestedMana: 0,
            currentHealth: 10_000,
            currentMana: 1_000,
            maximumHealth: 10_000,
            maximumMana: 1_000);
        var lethal = ElementalResonanceExecutionPolicy.AdjustIncomingDirectDamage(
            Direct(2), profile, state, 1_000, 500, 10_000, 1_000);
        Check.True(
            recovery.RequestedHealth == 220 &&
            recovery.BarrierAdded == 110 &&
            lethal.ApolloLethalProtectionApplied &&
            lethal.AdjustedDamage == 499 &&
            lethal.RemainingHealth == 1 &&
            lethal.ConsumedBarrier == 110 &&
            state.Barrier == 0,
            "Apollo amplifies recovery, banks bounded overheal, and consumes it at lethal damage");
    }

    private static void CheckDarkExecution()
    {
        var profile = ResonanceProfile(ElementKind.Dark, 10);
        var state = new ElementalResonanceState(10);
        var combatEvent = Direct(1);
        var pre = ElementalResonanceExecutionPolicy.AdjustOutgoingDirectDamage(
            combatEvent, profile, state, 1_000, 2_499, 10_000);
        var post = ElementalResonanceExecutionPolicy.ProcessCommittedDirectHit(
            combatEvent,
            profile,
            state,
            new ElementalStatusState(20),
            1_000,
            10_000,
            false,
            []);
        var kill = ElementalResonanceExecutionPolicy.ProcessCreditedKill(
            KillEvent(2), profile, state, 9_000, 900, 10_000, 1_000);
        Check.True(
            pre.HadesExecuteApplied && pre.AdjustedDamage == 1_120 &&
            post.SourceHealthRecovery == 20 &&
            kill.AppliedHealth == 800 && kill.AppliedMana == 80,
            "Hades preserves execute, capped applied-damage healing, and credited-kill restoration");
    }

    private static ElementalEquipmentProfile ResonanceProfile(
        ElementKind element,
        int pieces)
    {
        var raw = Enum.GetValues<ElementKind>()
            .ToDictionary(static value => value, static _ => default(ElementalEffectTotals));
        var counts = Enum.GetValues<ElementKind>()
            .ToDictionary(static value => value, static _ => 0);
        counts[element] = pieces;
        var active = Enum.GetValues<ElementKind>().ToDictionary(
            static value => value,
            value => ElementalResonanceCatalog.ActiveFor(value, counts[value]));
        return new(raw, counts, active);
    }

    private static DeterministicCombatEventContext Direct(
        ulong sequence,
        long time = 0) =>
        new(
            sequence, 7, 10, 20, time,
            CombatEventProvenance.DirectSkill,
            true, false, default);

    private static DeterministicCombatEventContext MovementEvent(
        ulong sequence,
        long time) =>
        new(
            sequence, 7, 10, 10, time,
            CombatEventProvenance.AcceptedMovement,
            true, false, default);

    private static DeterministicCombatEventContext RecoveryEvent(
        ulong sequence,
        long time) =>
        new(
            sequence, 7, 20, 20, time,
            CombatEventProvenance.Recovery,
            true, false, default);

    private static DeterministicCombatEventContext KillEvent(ulong sequence) =>
        new(
            sequence, 7, 10, 20, 0,
            CombatEventProvenance.CreditedKill,
            true, false, default);

    private static ResonanceTargetCandidate MonsterCandidate(
        long targetId,
        long distance) =>
        new(
            targetId,
            7,
            distance,
            true,
            false,
            ResonanceTargetAuthority.AuthoritativeMonster,
            default);
}
