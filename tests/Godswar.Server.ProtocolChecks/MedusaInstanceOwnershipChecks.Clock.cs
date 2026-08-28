using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckDescriptorTimeAndCapacityAuthority()
    {
        var descriptorCreatedAt = StartedAt
            .AddMinutes(17)
            .ToOffset(TimeSpan.FromHours(12));
        var map = CreateMap(
            MedusaEncounterDifficulty.Enhanced,
            createdAt: descriptorCreatedAt);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        Check.True(
            bound.Run.StartedAt ==
                descriptorCreatedAt.ToUniversalTime() &&
            bound.Mechanics.StartedAt == bound.Run.StartedAt &&
            bound.Run.Deadline ==
                bound.Run.StartedAt.Add(
                    MedusaIslandPolicy.TimeLimit),
            "the immutable descriptor creation time owns the run clock");

        var bounded = CreateMap(
            MedusaEncounterDifficulty.Enhanced,
            playerCapacity: 2);
        var rejected = Bind(
            bounded,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101, 102, 103]);
        Check.True(
            rejected.Outcome ==
                MedusaInstanceBindOutcome
                    .AdmittedRosterExceedsPlayerCapacity &&
            rejected.Snapshot is null &&
            !bounded.TryGetMedusaOwnershipSnapshot(out _) &&
            Bind(
                bounded,
                MedusaEncounterDifficulty.Enhanced,
                characters: [101, 102]).IsBound,
            "descriptor player capacity rejects an oversized roster " +
            "without consuming ownership");
    }

    private static void CheckCoupledOperationsAndPurePreview()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var pikeman = Binding(bound, "Final-Pikeman-1");
        var late = StartedAt.AddMinutes(20);
        var physical = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Normal,
            damage: 100);

        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                999,
                pikeman.Identity.ObjectId,
                pikeman.Identity.SpawnGeneration,
                late,
                out var foreignHit) &&
            foreignHit.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.CharacterNotAdmitted &&
            map.TryAbandonMedusaRun(
                999,
                late,
                out var foreignAbandon) &&
            foreignAbandon.RunOutcome ==
                MedusaRunAbandonOutcome.CharacterNotAdmitted &&
            map.TryPreviewMedusaOutgoingDamage(
                999,
                physical,
                out var foreignPreview) &&
            foreignPreview.MechanicsResult?.Outcome ==
                MedusaOutgoingDamageOutcome.CharacterNotAdmitted,
            "foreign hit, abandon, and preview identities reject");
        AssertCoupledAt(
            map,
            StartedAt,
            "invalid identities cannot move either owner clock");

        var hitAt = StartedAt.AddSeconds(1);
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                pikeman.Identity.ObjectId,
                pikeman.Identity.SpawnGeneration,
                hitAt,
                out var hit) &&
            hit.GateOutcome ==
                MedusaOwnedOperationGateOutcome.Delegated &&
            hit.RunClockOutcome == MedusaRunClockOutcome.Active &&
            hit.MechanicsClockResult?.Outcome ==
                MedusaMechanicsClockOutcome.Advanced &&
            hit.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            "an accepted hit advances both clocks before applying its effect");
        AssertCoupledAt(map, hitAt, "accepted hit");

        Check.True(
            map.TryGetMedusaOwnershipSnapshot(out var beforePreview),
            "ownership snapshot exists before pure preview");
        var effectBefore = beforePreview.Mechanics.Characters
            .Single().ActiveEffects.Single();
        var previewResolved = map.TryPreviewMedusaOutgoingDamage(
            101,
            physical,
            out var preview);
        var afterPreviewCaptured =
            map.TryGetMedusaOwnershipSnapshot(out var afterPreview);
        Check.True(
            previewResolved &&
            preview.GateOutcome ==
                MedusaOwnedOperationGateOutcome.Delegated &&
            preview.MechanicsResult is
            {
                Outcome: MedusaOutgoingDamageOutcome.Resolved,
                AppliedMultiplier: 10,
                Resolution.Damage: 1_000
            } &&
            afterPreviewCaptured,
            "an admitted physical outgoing preview resolves");
        var effectAfter = afterPreview.Mechanics.Characters
            .Single().ActiveEffects.Single();
        Check.True(
            beforePreview.Run.LastObservedAt ==
                afterPreview.Run.LastObservedAt &&
            beforePreview.Mechanics.LastObservedAt ==
                afterPreview.Mechanics.LastObservedAt &&
            effectAfter == effectBefore &&
            OwnershipSnapshotsValueEqual(
                beforePreview,
                afterPreview),
            "outgoing preview cannot advance time, expire effects, or " +
            "consume periodic state");

        var earlierDefeatAt = StartedAt.AddMilliseconds(500);
        Check.True(
            map.TryObserveMedusaTime(
                earlierDefeatAt,
                out var earlierObservation) &&
            earlierObservation.GateOutcome ==
                MedusaOwnedOperationGateOutcome.TimestampMovedBackward &&
            earlierObservation.RunOutcome ==
                MedusaRunClockOutcome.TimestampMovedBackward &&
            earlierObservation.MechanicsResult is null,
            "an earlier observation rejects after a future valid hit");
        AssertCoupledAt(
            map,
            hitAt,
            "earlier observation after future hit");

        var observedAt = StartedAt.AddSeconds(3);
        Check.True(
            map.TryObserveMedusaTime(observedAt, out var observed) &&
            observed.GateOutcome ==
                MedusaOwnedOperationGateOutcome.Delegated &&
            observed.RunOutcome == MedusaRunClockOutcome.Active &&
            observed.MechanicsResult?.Outcome ==
                MedusaMechanicsClockOutcome.Advanced,
            "explicit time observation advances both clocks");
        AssertCoupledAt(map, observedAt, "accepted observation");

        var abandonedAt = StartedAt.AddSeconds(4);
        Check.True(
            map.TryAbandonMedusaRun(
                101,
                abandonedAt,
                out var abandoned) &&
            abandoned.RunOutcome == MedusaRunAbandonOutcome.Exited &&
            abandoned.MechanicsClockResult?.Outcome ==
                MedusaMechanicsClockOutcome.Advanced,
            "accepted abandonment advances mechanics with the run");
        AssertCoupledAt(map, abandonedAt, "accepted abandonment");
    }

    private static void CheckDeadlineCouplingAndMechanicsGate()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var elite = Binding(bound, "E1-Elite");
        var deadline = bound.Run.Deadline;

        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                elite.Identity.ObjectId,
                elite.Identity.SpawnGeneration,
                deadline,
                out var exact) &&
            exact.GateOutcome ==
                MedusaOwnedOperationGateOutcome
                    .DeadlineBoundaryUnresolved &&
            exact.RunClockOutcome ==
                MedusaRunClockOutcome.DeadlineBoundaryUnresolved &&
            exact.MechanicsClockResult?.Outcome ==
                MedusaMechanicsClockOutcome
                    .DeadlineBoundaryUnresolved &&
            exact.MechanicsResult is null,
            "an exact-deadline hit advances both clocks but applies no mechanic");
        AssertCoupledAt(map, deadline, "exact deadline");
        Check.True(
            map.TryGetMedusaOwnershipSnapshot(out var atDeadline) &&
            atDeadline.Run.State == MedusaRunState.Active &&
            atDeadline.Mechanics.Characters.Single()
                .ActiveEffects.IsEmpty,
            "the unresolved boundary leaves no hit effect behind");

        var physical = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Normal,
            damage: 100);
        Check.True(
            map.TryPreviewMedusaOutgoingDamage(
                101,
                physical,
                out var gatedPreview) &&
            gatedPreview.GateOutcome ==
                MedusaOwnedOperationGateOutcome
                    .DeadlineBoundaryUnresolved &&
            gatedPreview.MechanicsResult is null,
            "the exact deadline gates even a pure outgoing preview");

        var afterDeadline = deadline.AddSeconds(1);
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                999,
                elite.Identity.ObjectId,
                elite.Identity.SpawnGeneration,
                afterDeadline,
                out var invalidLate) &&
            invalidLate.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.CharacterNotAdmitted,
            "an invalid post-deadline identity remains non-authoritative");
        AssertCoupledAt(
            map,
            deadline,
            "invalid post-deadline identity");

        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                elite.Identity.ObjectId,
                elite.Identity.SpawnGeneration,
                afterDeadline,
                out var timedOut) &&
            timedOut.GateOutcome ==
                MedusaOwnedOperationGateOutcome.TimedOut &&
            timedOut.RunClockOutcome ==
                MedusaRunClockOutcome.TimedOut &&
            timedOut.MechanicsClockResult?.Outcome ==
                MedusaMechanicsClockOutcome.Advanced &&
            timedOut.MechanicsResult is null,
            "the next valid observation times out both clocks without a hit");
        AssertCoupledAt(map, afterDeadline, "timed-out hit");

        var abandonMap = CreateMap(
            MedusaEncounterDifficulty.Enhanced);
        var abandonBound = Bind(
            abandonMap,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var abandonElite = Binding(abandonBound, "E1-Elite");
        _ = abandonMap.TryCommitOwnerMechanicForInvariantTest(
            101,
            abandonElite.Identity.ObjectId,
            abandonElite.Identity.SpawnGeneration,
            StartedAt.AddSeconds(1),
            out _);
        Check.True(
            abandonMap.TryAbandonMedusaRun(
                101,
                abandonBound.Run.Deadline,
                out var exactAbandon) &&
            exactAbandon.RunOutcome ==
                MedusaRunAbandonOutcome
                    .DeadlineBoundaryUnresolved &&
            exactAbandon.MechanicsClockResult?.Outcome ==
                MedusaMechanicsClockOutcome
                    .DeadlineBoundaryUnresolved,
            "exact-deadline abandonment advances both clocks without exiting");
        AssertCoupledAt(
            abandonMap,
            abandonBound.Run.Deadline,
            "exact-deadline abandonment");
        Check.True(
            abandonMap.TryGetMedusaOwnershipSnapshot(
                out var afterExactAbandon) &&
            afterExactAbandon.Run.State == MedusaRunState.Active &&
            afterExactAbandon.Mechanics.Characters.Single()
                .ActiveEffects.IsEmpty,
            "exact-deadline abandonment gates terminal mutation and mechanics");
    }

    private static void AssertCoupledAt(
        MapInstance map,
        DateTimeOffset expected,
        string operation)
    {
        Check.True(
            map.TryGetMedusaOwnershipSnapshot(out var snapshot) &&
            snapshot.Run.LastObservedAt == expected.ToUniversalTime() &&
            snapshot.Mechanics.LastObservedAt ==
                expected.ToUniversalTime(),
            $"{operation} keeps run and mechanics clocks coupled");
    }

    private static bool OwnershipSnapshotsValueEqual(
        MedusaInstanceOwnershipSnapshot left,
        MedusaInstanceOwnershipSnapshot right) =>
        left.WorldInstanceId == right.WorldInstanceId &&
        left.Difficulty == right.Difficulty &&
        left.ContentMapId == right.ContentMapId &&
        left.MonsterBindings.SequenceEqual(right.MonsterBindings) &&
        RunSnapshotsValueEqual(left.Run, right.Run) &&
        MechanicsSnapshotsValueEqual(left.Mechanics, right.Mechanics);

    private static bool RunSnapshotsValueEqual(
        MedusaRunSnapshot left,
        MedusaRunSnapshot right) =>
        left.WorldInstanceId == right.WorldInstanceId &&
        left.Difficulty == right.Difficulty &&
        left.ContentMapId == right.ContentMapId &&
        left.StartedAt == right.StartedAt &&
        left.Deadline == right.Deadline &&
        left.LastObservedAt == right.LastObservedAt &&
        left.State == right.State &&
        left.TeamScore == right.TeamScore &&
        left.AdmittedCharacterIds.SequenceEqual(
            right.AdmittedCharacterIds) &&
        left.Spawns.SequenceEqual(right.Spawns) &&
        left.CompletionMarker == right.CompletionMarker;

    private static bool MechanicsSnapshotsValueEqual(
        MedusaEncounterMechanicsSnapshot left,
        MedusaEncounterMechanicsSnapshot right) =>
        left.WorldInstanceId == right.WorldInstanceId &&
        left.Difficulty == right.Difficulty &&
        left.ContentMapId == right.ContentMapId &&
        left.StartedAt == right.StartedAt &&
        left.LastObservedAt == right.LastObservedAt &&
        left.OutstandingPeriodicDamage ==
            right.OutstandingPeriodicDamage &&
        left.Characters.Length == right.Characters.Length &&
        left.Characters.Zip(right.Characters).All(pair =>
            pair.First.CharacterId == pair.Second.CharacterId &&
            pair.First.EffectTarget == pair.Second.EffectTarget &&
            pair.First.ControlRestriction ==
                pair.Second.ControlRestriction &&
            pair.First.PhysicalOutgoingDamageMultiplier ==
                pair.Second.PhysicalOutgoingDamageMultiplier &&
            pair.First.MagicalOutgoingDamageMultiplier ==
                pair.Second.MagicalOutgoingDamageMultiplier &&
            pair.First.ActiveEffects.SequenceEqual(
                pair.Second.ActiveEffects));

    private static CombatResolution Resolution(
        CombatDamageChannel channel,
        CombatHitOutcome outcome,
        uint damage) => new(
        FormulaVersion: 23,
        EventId: 88,
        TargetOrder: 3,
        channel,
        outcome,
        damage,
        new CombatRollEvidence(1, 2, 3, 4),
        new CombatDamageEvidence(
            Attack: 100,
            EffectiveDefense: 20,
            AttackAfterDefense: 80,
            SkillCoreDamage: 80,
            DamageAfterTypedBonus: 80,
            CriticalBonusDamage: 0,
            DamageWithAppend: 80,
            DamageAfterReduction: 80,
            DamageAfterTakenIncrease: 80,
            DamageAfterAbsorption: damage));
}
