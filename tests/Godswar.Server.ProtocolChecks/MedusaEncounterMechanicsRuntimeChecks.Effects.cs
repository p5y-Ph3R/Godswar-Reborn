using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaEncounterMechanicsRuntimeChecks
{
    private static void CheckBleedCadenceAndRefresh()
    {
        var runtime = CreateRuntime();
        var chrysaor = Source(runtime, "Chrysaor");
        var start = runtime.StartedAt;
        var applied = Hit(runtime, chrysaor, 101, start.AddSeconds(1));
        var beforePreview = runtime.Snapshot();
        var outgoingPreview = runtime.PreviewOutgoingDamage(
            101,
            Resolution(
                CombatDamageChannel.Physical,
                CombatHitOutcome.Normal,
                100));
        var afterPreview = runtime.Snapshot();

        Check.True(
            applied.Effect?.Definition.Bleed is
            {
                DamagePerTick: 200,
                MaximumTicks: 7
            } &&
            outgoingPreview.Outcome ==
                MedusaOutgoingDamageOutcome.Resolved &&
            beforePreview.LastObservedAt == afterPreview.LastObservedAt &&
            Character(beforePreview, 101).ActiveEffects.Single() ==
                Character(afterPreview, 101).ActiveEffects.Single() &&
            applied.Effect is
            {
                NextPeriodicTickAt: var nextTickAt
            } &&
            nextTickAt == start.AddSeconds(3),
            "outgoing preview is pure and bleed first ticks two seconds later");
        Check.True(
            runtime.ObserveTime(start.AddMilliseconds(2_999))
                .PeriodicDamage is null,
            "bleed does not tick before its numeric two-second interval");

        var first = runtime.ObserveTime(start.AddSeconds(3));
        var firstReservation = first.PeriodicDamage!;
        var reacquired = runtime.ObserveTime(start.AddSeconds(16));
        Check.True(
            first.Outcome ==
                MedusaMechanicsClockOutcome.PeriodicDamageRequired &&
            ReferenceEquals(firstReservation, reacquired.PeriodicDamage) &&
            firstReservation.Identity is
            {
                TargetCharacterId: 101,
                TickNumber: 1,
                Damage: 200,
                DueAt: var firstDueAt
            } &&
            firstDueAt == start.AddSeconds(3) &&
            runtime.Snapshot().LastObservedAt ==
                start.AddMilliseconds(2_999),
            "reserve is non-consuming and reacquires one stable tick");

        var ticks = new List<MedusaPeriodicDamageIdentity>(7);
        for (var ordinal = 1; ordinal <= 7; ordinal++)
        {
            var reservation = runtime
                .ObserveTime(start.AddSeconds(16))
                .PeriodicDamage!;
            ticks.Add(reservation.Identity);
            Check.True(
                runtime.CompletePeriodicDamageApplied(reservation) ==
                    MedusaPeriodicDamageDispositionOutcome.Applied,
                "an exact reserved tick advances once after HP proof");
        }
        var expired = runtime.ObserveTime(start.AddSeconds(16));
        Check.True(
            ticks.Select(static tick => tick.TickNumber)
                .SequenceEqual(Enumerable.Range(1, 7)) &&
            ticks.Select(static tick => tick.DueAt)
                .SequenceEqual(Enumerable.Range(0, 7)
                    .Select(index => start.AddSeconds(3 + index * 2))) &&
            ticks.Sum(static tick => tick.Damage) == 1_400 &&
            expired.Outcome == MedusaMechanicsClockOutcome.Advanced &&
            expired.PeriodicDamage is null &&
            Character(runtime.Snapshot(), 101).ActiveEffects.IsEmpty,
            "status 18 consumes exactly seven ordered ticks and no expiry tick");

        var refreshedRuntime = CreateRuntime();
        var source = Source(refreshedRuntime, "Chrysaor");
        var original = Hit(
            refreshedRuntime,
            source,
            101,
            start.AddSeconds(1));
        var beforeDueRefresh = Hit(
            refreshedRuntime,
            source,
            101,
            start.AddSeconds(2));
        Check.True(
            beforeDueRefresh.Outcome ==
                MedusaMechanicHitOutcome.Refreshed &&
            beforeDueRefresh.Effect?.ApplicationSequence !=
                original.Effect?.ApplicationSequence &&
            beforeDueRefresh.Effect?.NextPeriodicTickAt ==
                start.AddSeconds(4) &&
            beforeDueRefresh.Effect?.ExpiresAt == start.AddSeconds(17),
            "refresh before due replaces the old application and cadence");
        var dueBeforeEqualRefresh = refreshedRuntime.CommitMonsterHit(
            101,
            source.ObjectId,
            source.SpawnGeneration,
            start.AddSeconds(4));
        Check.True(
            dueBeforeEqualRefresh.Outcome ==
                MedusaMechanicHitOutcome.PeriodicDamageRequired &&
            dueBeforeEqualRefresh.PeriodicDamage?.Identity is
            {
                TickNumber: 1,
                ApplicationSequence: var dueSequence
            } &&
            dueSequence == beforeDueRefresh.Effect?.ApplicationSequence,
            "a refresh at equality hands off the old due tick first");
        _ = refreshedRuntime.CompletePeriodicDamageApplied(
            dueBeforeEqualRefresh.PeriodicDamage);
        var equalRefresh = Hit(
            refreshedRuntime,
            source,
            101,
            start.AddSeconds(4));
        Check.True(
            equalRefresh.Outcome == MedusaMechanicHitOutcome.Refreshed &&
            equalRefresh.Effect?.NextPeriodicTickAt ==
                start.AddSeconds(6),
            "the exact refresh retries after the equal-time tick is consumed");

        var isolated = CreateRuntime([101, 102]);
        var isolatedSource = Source(isolated, "Chrysaor");
        _ = Hit(isolated, isolatedSource, 102, start.AddSeconds(1));
        _ = Hit(isolated, isolatedSource, 101, start.AddSeconds(1));
        var simultaneousOne = isolated.ObserveTime(start.AddSeconds(3));
        _ = isolated.CompletePeriodicDamageApplied(
            simultaneousOne.PeriodicDamage);
        var simultaneousTwo = isolated.ObserveTime(start.AddSeconds(3));
        Check.True(
            simultaneousOne.PeriodicDamage?.Identity.TargetCharacterId ==
                101 &&
            simultaneousTwo.PeriodicDamage?.Identity.TargetCharacterId ==
                102,
            "simultaneous ticks are deterministically ordered by character");
    }

    private static void CheckOutgoingAmplifiers()
    {
        var runtime = CreateRuntime([101, 102]);
        var pikemanOne = Source(runtime, "Final-Pikeman-1");
        var pikemanTwo = Source(runtime, "Final-Pikeman-2");
        var axeman = Source(runtime, "Final-Axeman-1");
        var start = runtime.StartedAt;

        _ = Hit(runtime, pikemanOne, 101, start.AddSeconds(1));
        var physicalSource = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Normal,
            100);
        var physical = runtime.PreviewOutgoingDamage(
            101,
            physicalSource);
        var magicalSource = Resolution(
            CombatDamageChannel.Magic,
            CombatHitOutcome.Critical,
            100);
        var magicalBefore = runtime.PreviewOutgoingDamage(
            101,
            magicalSource);
        Check.True(
            physical.Outcome == MedusaOutgoingDamageOutcome.Resolved &&
            physical.AppliedMultiplier == 10 &&
            physical.Resolution == physicalSource with { Damage = 1_000 } &&
            magicalBefore.AppliedMultiplier == 1 &&
            magicalBefore.Resolution == magicalSource,
            "Pikeman multiplies only typed physical outgoing damage");

        _ = Hit(runtime, axeman, 101, start.AddSeconds(3));
        var magical = runtime.PreviewOutgoingDamage(
            101,
            magicalSource);
        var otherCharacter = runtime.PreviewOutgoingDamage(
            102,
            physicalSource);
        Check.True(
            magical.AppliedMultiplier == 10 &&
            magical.Resolution.Damage == 1_000 &&
            otherCharacter.AppliedMultiplier == 1 &&
            otherCharacter.Resolution == physicalSource,
            "Axeman is magical-only and amplifier state is per character");

        var veryLarge = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Normal,
            uint.MaxValue);
        var saturated = runtime.PreviewOutgoingDamage(
            101,
            veryLarge);
        var missSource = Resolution(
            CombatDamageChannel.Physical,
            CombatHitOutcome.Miss,
            999);
        var miss = runtime.PreviewOutgoingDamage(
            101,
            missSource);
        Check.True(
            saturated.AppliedMultiplier == 10 &&
            saturated.Resolution.Damage == uint.MaxValue &&
            miss.AppliedMultiplier == 1 &&
            miss.Resolution == missSource,
            "10x multiplication saturates and never rewrites a miss");

        var refreshed = Hit(
            runtime,
            pikemanTwo,
            101,
            start.AddSeconds(20));
        Check.True(
            refreshed.Outcome == MedusaMechanicHitOutcome.Refreshed &&
            refreshed.Effect?.ExpiresAt == start.AddSeconds(50) &&
            Character(runtime.Snapshot(), 101)
                .PhysicalOutgoingDamageMultiplier == 10,
            "a second Pikeman refreshes rather than stacking above 10x");
        _ = runtime.ObserveTime(start.AddSeconds(31));
        Check.Equal(
            10,
            Character(runtime.Snapshot(), 101)
                .PhysicalOutgoingDamageMultiplier,
            "refreshed physical amplifier survives its former deadline");
        _ = runtime.ObserveTime(start.AddSeconds(50));
        Check.True(
            Character(runtime.Snapshot(), 101)
                .PhysicalOutgoingDamageMultiplier == 1 &&
            Character(runtime.Snapshot(), 101)
                .MagicalOutgoingDamageMultiplier == 1,
            "amplifiers expire at their exclusive 30-second boundaries");
    }

    private static void CheckMonotonicFailureBoundaries()
    {
        var runtime = CreateRuntime();
        var chrysaor = Source(runtime, "Chrysaor");
        var noMechanic = Source(runtime, "E13-Elite");
        var stun = Source(runtime, "E1-Elite");
        var start = runtime.StartedAt;
        _ = Hit(runtime, chrysaor, 101, start.AddSeconds(10));
        var baseline = runtime.Snapshot();

        var foreign = Hit(runtime, chrysaor, 999, start.AddSeconds(30));
        var unknown = runtime.CommitMonsterHit(
            101,
            uint.MaxValue,
            1,
            start.AddSeconds(30));
        var stale = runtime.CommitMonsterHit(
            101,
            chrysaor.ObjectId,
            checked(chrysaor.SpawnGeneration + 1),
            start.AddSeconds(30));
        var inert = Hit(runtime, noMechanic, 101, start.AddSeconds(11));
        var backward = Hit(runtime, chrysaor, 101, start.AddSeconds(9));
        var overflowRuntime = CreateRuntime();
        var overflow = Hit(
            overflowRuntime,
            stun,
            101,
            DateTimeOffset.MaxValue.AddSeconds(-1));

        Check.True(
            foreign.Outcome ==
                MedusaMechanicHitOutcome.CharacterNotAdmitted &&
            unknown.Outcome == MedusaMechanicHitOutcome.UnknownMonster &&
            stale.Outcome ==
                MedusaMechanicHitOutcome.StaleMonsterGeneration &&
            inert.Outcome ==
                MedusaMechanicHitOutcome.MonsterHasNoAuthoredMechanic &&
            backward.Outcome ==
                MedusaMechanicHitOutcome.TimestampMovedBackward &&
            overflow.Outcome ==
                MedusaMechanicHitOutcome.EffectWindowUnrepresentable &&
            runtime.Snapshot().LastObservedAt == baseline.LastObservedAt &&
            Character(runtime.Snapshot(), 101).ActiveEffects[0]
                .ApplicationSequence ==
            Character(baseline, 101).ActiveEffects[0].ApplicationSequence,
            "all unauthorized or invalid hits reject before clock/effect mutation");

        var unknownChannel = Resolution(
            (CombatDamageChannel)byte.MaxValue,
            CombatHitOutcome.Normal,
            100);
        var badDamage = runtime.PreviewOutgoingDamage(
            101,
            unknownChannel);
        var foreignDamage = runtime.PreviewOutgoingDamage(
            999,
            Resolution(
                CombatDamageChannel.Physical,
                CombatHitOutcome.Normal,
                100));
        Check.True(
            badDamage.Outcome ==
                MedusaOutgoingDamageOutcome.UnknownDamageChannel &&
            foreignDamage.Outcome ==
                MedusaOutgoingDamageOutcome.CharacterNotAdmitted &&
            runtime.Snapshot().LastObservedAt == baseline.LastObservedAt,
            "invalid typed outgoing resolutions also cannot mutate the clock");

        var due = runtime.ObserveTime(start.AddSeconds(12));
        Check.True(
            due.PeriodicDamage?.Identity is
                { TargetCharacterId: 101, Damage: 200, TickNumber: 1 } &&
            runtime.Snapshot().LastObservedAt == baseline.LastObservedAt,
            "the next valid UTC observation reserves without consuming");

        var offsetRuntime = CreateRuntime();
        var offsetSource = Source(offsetRuntime, "E1-Elite");
        var offsetHit = Hit(
            offsetRuntime,
            offsetSource,
            101,
            start.AddSeconds(1).ToOffset(TimeSpan.FromHours(12)));
        Check.True(
            offsetHit.Effect?.AppliedAt == start.AddSeconds(1) &&
            offsetRuntime.Snapshot().LastObservedAt == start.AddSeconds(1),
            "equivalent offset timestamps are normalized to UTC");
    }

    private static void CheckSourceRetirement()
    {
        var runtime = CreateRuntime();
        var chrysaor = Source(runtime, "Chrysaor");
        var start = runtime.StartedAt;
        _ = Hit(runtime, chrysaor, 101, start.AddSeconds(1));
        var retired = runtime.RetireMonster(
            chrysaor.ObjectId,
            chrysaor.SpawnGeneration,
            start.AddSeconds(2));
        var rejected = Hit(runtime, chrysaor, 101, start.AddSeconds(3));

        Check.True(
            retired.Outcome ==
                MedusaMechanicSourceRetireOutcome.Retired &&
            rejected.Outcome == MedusaMechanicHitOutcome.MonsterRetired &&
            runtime.Snapshot().LastObservedAt == start.AddSeconds(2),
            "retirement fences future source hits without accepting their time");
        var existingBleed = runtime.ObserveTime(start.AddSeconds(3));
        Check.True(
            existingBleed.PeriodicDamage?.Identity is
                { SourceRosterSpawnId: "Chrysaor", TickNumber: 1 },
            "an already committed bleed continues after its source retires");
        _ = runtime.CompletePeriodicDamageApplied(
            existingBleed.PeriodicDamage);

        var duplicate = runtime.RetireMonster(
            chrysaor.ObjectId,
            chrysaor.SpawnGeneration,
            start.AddSeconds(30));
        Check.True(
            duplicate.Outcome ==
                MedusaMechanicSourceRetireOutcome.AlreadyRetired &&
            runtime.Snapshot().LastObservedAt == start.AddSeconds(3),
            "duplicate retirement is idempotent and non-authoritative");
    }

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
