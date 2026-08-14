using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static void CheckDelayedBurnAndWindResistance()
    {
        var status = new ElementalStatusState(ownerCharacterId: 200);
        var first = BurnApplication(
            sourceCharacterId: 100,
            sourceEventId: 1,
            appliedAt: 0,
            totalDamage: 40);
        var second = BurnApplication(
            sourceCharacterId: 101,
            sourceEventId: 2,
            appliedAt: 5_000,
            totalDamage: 80);
        Check.True(
            status.TryApply(first) && status.TryApply(second),
            "a new Burn can replace an expired Burn without losing overdue ticks");

        var delayed = status.CollectDuePeriodicDamage(5_000);
        var notDue = status.CollectDuePeriodicDamage(5_000);
        var replacement = status.CollectDuePeriodicDamage(9_000);
        Check.True(
            delayed.Count == 4 &&
            delayed.All(static value =>
                value.SourceCharacterId == 100 &&
                value.SourceEventId == 1) &&
            delayed.Sum(static value => value.Damage) == 40 &&
            notDue.Count == 0 &&
            replacement.Count == 4 &&
            replacement.All(static value =>
                value.SourceCharacterId == 101 &&
                value.SourceEventId == 2) &&
            replacement.Sum(static value => value.Damage) == 80,
            "the delayed target-owner loop drains old and replacement Burn batches exactly once");

        var detonation = new ElementalStatusState(ownerCharacterId: 200);
        Check.True(
            detonation.TryApply(first) &&
            detonation.ConsumeRemainingBurn(5_000) == 40 &&
            detonation.ConsumeRemainingBurn(5_000) == 0 &&
            detonation.CollectDuePeriodicDamage(5_000).Count == 0,
            "detonation consumes expired uncollected Burn damage once instead of dropping it");

        var target = WithTotals(
            WithTotals(
                ExecutionProfile(
                    ElementKind.Water,
                    new ElementalEffectTotals(0, 0, 0)),
                ElementKind.Water,
                new ElementalEffectTotals(0, 2_000, 0)),
            ElementKind.Wind,
            new ElementalEffectTotals(0, 7_000, 0));
        var drench = FindDirectApplication(
            ElementKind.Water,
            ExecutionProfile(
                ElementKind.Water,
                new ElementalEffectTotals(1_000, 0, 2_000)),
            target,
            AuthoredElementalCombatV1.EffectTuning,
            sourceId: 100,
            targetId: 200,
            appliedDamage: 10_000);
        Check.True(
            drench.TargetResistanceBasisPoints == 7_000 &&
            drench.EffectivePotencyBasisPoints == 300,
            "Drench uses the stronger Water or Wind slow resistance once");

        CheckPrometheusCommitOrdering();
    }

    private static void CheckPrometheusCommitOrdering()
    {
        var pveNoPrior = CommitFifthFireHit(
            isPvp: false,
            withWeakPriorBurn: false);
        var pvpNoPrior = CommitFifthFireHit(
            isPvp: true,
            withWeakPriorBurn: false);
        var pveWeakPrior = CommitFifthFireHit(
            isPvp: false,
            withWeakPriorBurn: true);
        var pvpWeakPrior = CommitFifthFireHit(
            isPvp: true,
            withWeakPriorBurn: true);

        Check.True(
            pveNoPrior.Resonance.BurnDetonated &&
            pveNoPrior.Resonance.DetonatedBurnDamage == 1_200 &&
            pvpNoPrior.Resonance.DetonatedBurnDamage == 1_200 &&
            pveWeakPrior.Resonance.DetonatedBurnDamage == 1_300 &&
            pvpWeakPrior.Resonance.DetonatedBurnDamage == 1_300 &&
            pveNoPrior.ElementalApplication is not null &&
            !pveNoPrior.ElementalApplicationAccepted &&
            pvpNoPrior.ElementalApplication is not null &&
            !pvpNoPrior.ElementalApplicationAccepted,
            "PvE/PvP fifth hits detonate only prior Burn, then arbitrate the current generic Burn");
    }

    private static ElementalDirectHitCommitResult CommitFifthFireHit(
        bool isPvp,
        bool withWeakPriorBurn)
    {
        var sourceProfile = ExecutionProfile(
            ElementKind.Fire,
            new ElementalEffectTotals(1_000, 0, 2_000),
            pieces: 10);
        var targetProfile = ExecutionProfile(ElementKind.Fire, default);
        var sourceState = new ElementalResonanceState(ownerCharacterId: 100);
        var primeStatuses = new ElementalStatusState(ownerCharacterId: 200);
        var admission = isPvp ? FirePvpAdmission() : default;
        for (ulong ordinal = 1; ordinal <= 4; ordinal++)
        {
            _ = ElementalDirectHitCommitPolicy.Commit(
                DirectEvent(
                    1_000_000 + ordinal,
                    100,
                    200,
                    isPvp,
                    admission),
                sourceProfile,
                sourceState,
                targetProfile,
                primeStatuses,
                authoredElement: null,
                AuthoredElementalCombatV1.EffectTuning,
                appliedDirectDamage: 10_000,
                sourceMaximumHealth: 10_000,
                primaryTargetIsBoss: false,
                additionalTargets: []);
        }

        var targetStatuses = new ElementalStatusState(ownerCharacterId: 200);
        if (withWeakPriorBurn)
        {
            Check.True(
                targetStatuses.TryApply(BurnApplication(
                    sourceCharacterId: 999,
                    sourceEventId: 999,
                    appliedAt: 0,
                    totalDamage: 100)),
                "weak prior Burn fixture applies");
        }

        var fifthEvent = FindFireApplicationEvent(
            sourceProfile,
            targetProfile,
            isPvp,
            admission);
        return ElementalDirectHitCommitPolicy.Commit(
            fifthEvent,
            sourceProfile,
            sourceState,
            targetProfile,
            targetStatuses,
            ElementKind.Fire,
            AuthoredElementalCombatV1.EffectTuning,
            appliedDirectDamage: 10_000,
            sourceMaximumHealth: 10_000,
            primaryTargetIsBoss: false,
            additionalTargets: []);
    }

    private static DeterministicCombatEventContext FindFireApplicationEvent(
        ElementalEquipmentProfile sourceProfile,
        ElementalEquipmentProfile targetProfile,
        bool isPvp,
        PvpEligibilityResult admission)
    {
        for (ulong eventId = 1; eventId <= 100_000; eventId++)
        {
            var combatEvent = DirectEvent(
                eventId,
                100,
                200,
                isPvp,
                admission);
            if (ElementalEffectExecutionPolicy.TryPlanDirectApplication(
                    combatEvent,
                    ElementKind.Fire,
                    sourceProfile,
                    targetProfile,
                    AuthoredElementalCombatV1.EffectTuning,
                    appliedDirectDamage: 10_000,
                    out _))
            {
                return combatEvent;
            }
        }

        throw new InvalidOperationException(
            "No deterministic Fire application event was found.");
    }

    private static PvpEligibilityResult FirePvpAdmission() =>
        new(
            Allowed: true,
            PvpEligibilityFailure.None,
            PvpEntitlementKind.MutualDuel,
            PvpCombatCaps.Current,
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            AttackerCharacterId: 100,
            TargetCharacterId: 200,
            MapId: 7);

    private static ElementalEffectApplication BurnApplication(
        long sourceCharacterId,
        ulong sourceEventId,
        long appliedAt,
        long totalDamage) =>
        new(
            ElementKind.Fire,
            ElementalEffectKind.Burn,
            sourceCharacterId,
            TargetCharacterId: 200,
            sourceEventId,
            appliedAt,
            ExpiresAtMilliseconds: checked(appliedAt + 4_000),
            EffectivePotencyBasisPoints: 1_000,
            ApplicationChanceBasisPoints: 10_000,
            TargetResistanceBasisPoints: 0,
            totalDamage,
            PeriodicTickCount: 4,
            CombatEventProvenance.ElementalStatus);
}
