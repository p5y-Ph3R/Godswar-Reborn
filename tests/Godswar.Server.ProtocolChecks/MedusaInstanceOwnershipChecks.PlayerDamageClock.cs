using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckDamageIdentityAndClockPreflight()
    {
        var identity = CreateAttachmentFixture();
        Check.True(AttachAuthored(identity).IsAttached,
            "damage identity fixture attaches");
        var target = FindMonster(identity.Map, "Stheno");
        var before = OwnershipState(identity.Map, target.ObjectId);

        var foreign = CommitTypedDamageWithExpectedIdentity(
            identity.Map,
            target,
            sessionCharacterId: 101,
            attackerCharacterId: 999,
            target.SpawnGeneration,
            target.HealthRevision,
            StartedAt.AddSeconds(1),
            Resolution(CombatDamageChannel.Physical, 100));
        var staleGeneration = CommitTypedDamageWithExpectedIdentity(
                identity.Map,
                target,
                sessionCharacterId: 101,
                attackerCharacterId: 101,
                checked(target.SpawnGeneration + 1),
                target.HealthRevision,
                StartedAt.AddSeconds(1),
                Resolution(CombatDamageChannel.Physical, 100));
        var staleHealth = CommitTypedDamageWithExpectedIdentity(
                identity.Map,
                target,
                sessionCharacterId: 101,
                attackerCharacterId: 101,
                target.SpawnGeneration,
                checked(target.HealthRevision + 1),
                StartedAt.AddSeconds(1),
                Resolution(CombatDamageChannel.Physical, 100));
        var invalidChannel = CommitTypedDamageWithExpectedIdentity(
                identity.Map,
                target,
                sessionCharacterId: 101,
                attackerCharacterId: 101,
                target.SpawnGeneration,
                target.HealthRevision,
                StartedAt.AddSeconds(1),
            Resolution((CombatDamageChannel)byte.MaxValue, 100));

        Check.True(
            foreign.Outcome ==
                MedusaPlayerMonsterDamageOutcome
                    .CurrentMembershipRequired &&
            staleGeneration.Outcome ==
                MedusaPlayerMonsterDamageOutcome
                    .StaleMonsterGeneration &&
            staleHealth.Outcome ==
                MedusaPlayerMonsterDamageOutcome.StaleHealthRevision &&
            invalidChannel.Outcome ==
                MedusaPlayerMonsterDamageOutcome.InvalidResolution &&
            OwnershipState(identity.Map, target.ObjectId) == before,
            "foreign, stale generation/revision, and channelless attacks reject before HP or run mutation");

        var backwards = CreateAttachmentFixture();
        Check.True(AttachAuthored(backwards).IsAttached,
            "backwards damage fixture attaches");
        ApplyCarrierHit(
            backwards.Map,
            "Final-Pikeman-1",
            101,
            StartedAt.AddSeconds(5));
        var backwardsTarget = FindMonster(backwards.Map, "Stheno");
        var backwardsBefore = OwnershipState(
            backwards.Map,
            backwardsTarget.ObjectId);
        var movedBack = CommitTypedDamage(
            backwards.Map,
            backwardsTarget,
            101,
            CombatDamageChannel.Physical,
            100,
            StartedAt.AddSeconds(4));
        Check.True(
            movedBack.Outcome ==
                MedusaPlayerMonsterDamageOutcome.TimestampMovedBackward &&
            OwnershipState(backwards.Map, backwardsTarget.ObjectId) ==
                backwardsBefore,
            "backwards damage time rejects before HP and owner mutation");

        CheckRejectedClockReconcilesExactly(
            StartedAt + MedusaIslandPolicy.TimeLimit,
            MedusaPlayerMonsterDamageOutcome
                .DeadlineBoundaryUnresolved,
            "exact unresolved deadline");
        CheckRejectedClockReconcilesExactly(
            StartedAt + MedusaIslandPolicy.TimeLimit +
                TimeSpan.FromTicks(1),
            MedusaPlayerMonsterDamageOutcome.TimedOut,
            "post-deadline timeout");
    }

    private static void CheckRejectedClockReconcilesExactly(
        DateTimeOffset committedAt,
        MedusaPlayerMonsterDamageOutcome expected,
        string description)
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            $"{description} fixture attaches");
        var target = FindMonster(fixture.Map, "Medusa");
        var before = OwnershipState(fixture.Map, target.ObjectId);
        var rejected = CommitTypedDamage(
            fixture.Map,
            target,
            101,
            CombatDamageChannel.Magic,
            100,
            committedAt);
        Check.True(
            rejected.Outcome == expected &&
            rejected.DamageResult is null &&
            rejected.Defeat is null &&
            fixture.Map.TryGetMedusaOwnershipSnapshot(out var ownership) &&
            ownership.Run.LastObservedAt == committedAt &&
            ownership.Mechanics.LastObservedAt == committedAt &&
            ownership.Run.State == (expected ==
                MedusaPlayerMonsterDamageOutcome.TimedOut
                    ? MedusaRunState.TimedOut
                    : MedusaRunState.Active) &&
            OwnershipState(fixture.Map, target.ObjectId) is var after &&
            after.Health == before.Health &&
            after.HealthRevision == before.HealthRevision &&
            after.TeamScore == before.TeamScore,
            $"{description} reconciles both clocks without HP, score, or defeat");
    }

    private static void CheckLethalDamageClaimsExactlyOnce()
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            "lethal transaction fixture attaches");
        var target = FindMonster(fixture.Map, "E13-Elite");
        var expectedScore = MedusaIslandPolicy.ScoreForDefeat(
            MedusaMonsterRank.Elite);
        var first = CommitTypedDamage(
            fixture.Map,
            target,
            101,
            CombatDamageChannel.Physical,
            uint.MaxValue,
            StartedAt.AddSeconds(1));

        Check.True(
            first.Outcome ==
                MedusaPlayerMonsterDamageOutcome.AppliedMedusa &&
            first.DamageResult is
            {
                Killed: true,
                AfterHealth: 0,
                Monster.RespawnAt: null
            } &&
            first.Defeat is
            {
                Claim.Outcome: MedusaDefeatClaimOutcome.Applied,
                Claim.ScoreAwarded: var awarded,
                Claim.TeamScore: var score,
                SourceRetirement.Outcome:
                    MedusaMechanicSourceRetireOutcome.Retired,
                SourceRetirement.PeriodicDamage: null,
                MechanicsClockResult: null
            } &&
            awarded == expectedScore &&
            score == expectedScore,
            "one typed lethal HP transition claims the roster defeat under the same owner gates");

        var duplicate = CommitTypedDamage(
            fixture.Map,
            target,
            101,
            CombatDamageChannel.Physical,
            uint.MaxValue,
            StartedAt.AddSeconds(2));
        Check.True(
            duplicate.Outcome ==
                MedusaPlayerMonsterDamageOutcome.StaleHealthRevision &&
            duplicate.DamageResult is null &&
            duplicate.Defeat is null &&
            fixture.Map.TryGetMonsterSnapshot(
                target.ObjectId,
                out var dead) &&
            dead.CurrentHealth == 0 &&
            dead.HealthRevision == target.HealthRevision + 1 &&
            fixture.Map.TryGetMedusaOwnershipSnapshot(out var ownership) &&
            ownership.Run.TeamScore == expectedScore &&
            ownership.Run.Spawns.Single(spawn =>
                spawn.RosterSpawnId == "E13-Elite").Defeated,
            "a replay cannot mutate HP or score and the defeat is claimed exactly once");
    }

    private static void CheckDamageDoesNotConsumePeriodicEvents()
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            "periodic handoff fixture attaches");
        var ownership = fixture.Map.TryGetMedusaOwnershipSnapshot(
            out var bound)
            ? bound
            : throw new InvalidOperationException(
                "Periodic handoff fixture lost ownership.");
        var bleedSource = Binding(ownership, "E9-Elite");
        Check.True(
            fixture.Map.TryCommitOwnerMechanicForInvariantTest(
                101,
                bleedSource.Identity.ObjectId,
                bleedSource.Identity.SpawnGeneration,
                StartedAt.AddSeconds(1),
                out var bleed) &&
            bleed.MechanicsResult?.Effect is
            {
                EmittedPeriodicTicks: 0,
                NextPeriodicTickAt: not null
            },
            "bleed source creates a future periodic event");

        var target = FindMonster(fixture.Map, "Medusa");
        var before = PeriodicState(fixture.Map, target.ObjectId);
        var blockedNonlethal = CommitTypedDamage(
            fixture.Map,
            target,
            101,
            CombatDamageChannel.Magic,
            100,
            StartedAt.AddSeconds(4));
        Check.True(
            blockedNonlethal.Outcome ==
                MedusaPlayerMonsterDamageOutcome
                    .PeriodicDamageHandoffUnavailable &&
            blockedNonlethal.DamageResult is null &&
            PeriodicState(fixture.Map, target.ObjectId) == before,
            "a due tick blocks nonlethal HP before any owner clock or monster mutation");

        Check.True(
            fixture.Map.TryObserveMedusaTime(
                StartedAt.AddSeconds(4),
                out var clock) &&
            clock.MechanicsResult?.PeriodicDamage is { } reservation &&
            TryCompletePeriodicDamageForProtocolCheck(
                fixture.Map,
                reservation,
                terminal: false,
                out var tickDisposition) &&
            tickDisposition ==
                MedusaPeriodicDamageDispositionOutcome.Applied,
            "the exact blocked tick is dispositioned before action retry");
        var retryTarget = fixture.Map.TryGetMonsterSnapshot(
            target.ObjectId,
            out var currentTarget)
            ? currentTarget
            : throw new InvalidOperationException(
                "Periodic target disappeared before exact retry.");
        var nonlethal = CommitTypedDamage(
            fixture.Map,
            retryTarget,
            101,
            CombatDamageChannel.Magic,
            100,
            StartedAt.AddSeconds(4));
        var afterNonlethal = PeriodicState(
            fixture.Map,
            target.ObjectId);
        Check.True(
            nonlethal is
            {
                Applied: true,
                DamageResult.HealthMutation: not null
            } &&
            nonlethal.DamageResult.BeforeHealth == before.Health &&
            nonlethal.DamageResult.AfterHealth ==
                before.Health - nonlethal.Resolution.Damage &&
            afterNonlethal.HealthRevision == before.HealthRevision + 1 &&
            afterNonlethal.EmittedTicks == 1 &&
            afterNonlethal.NextTickAt == StartedAt.AddSeconds(5),
            "same-timestamp retry mutates HP exactly once and preserves the next tick");
        AssertCoupledAt(
            fixture.Map,
            StartedAt.AddSeconds(4),
            "periodic-before-nonlethal-retry");

        var refreshedTarget = fixture.Map.TryGetMonsterSnapshot(
            target.ObjectId,
            out var refreshed)
            ? refreshed
            : throw new InvalidOperationException(
                "Periodic target disappeared after nonlethal damage.");
        var beforeLethal = PeriodicState(
            fixture.Map,
            target.ObjectId);
        var lethal = CommitTypedDamage(
            fixture.Map,
            refreshedTarget,
            101,
            CombatDamageChannel.Magic,
            uint.MaxValue,
            StartedAt.AddSeconds(5));
        Check.True(
            lethal.Outcome ==
                MedusaPlayerMonsterDamageOutcome
                    .PeriodicDamageHandoffUnavailable &&
            lethal.DamageResult is null &&
            lethal.Defeat is null &&
            PeriodicState(fixture.Map, target.ObjectId) == beforeLethal,
            "the next due tick also blocks lethal damage before HP");

        Check.True(
            fixture.Map.TryObserveMedusaTime(
                StartedAt.AddSeconds(5),
                out var nextClock) &&
            nextClock.MechanicsResult?.PeriodicDamage is not null,
            "the rejected lethal transaction leaves the due bleed event available to its owner clock consumer");
    }

    private static (
        uint Health,
        ulong HealthRevision,
        int TeamScore,
        DateTimeOffset RunClock,
        DateTimeOffset MechanicsClock) OwnershipState(
            Godswar.Server.Game.MapInstance map,
            uint objectId)
    {
        if (!map.TryGetMonsterSnapshot(objectId, out var monster) ||
            !map.TryGetMedusaOwnershipSnapshot(out var ownership))
        {
            throw new InvalidOperationException(
                "Damage fixture state is unavailable.");
        }

        return (
            monster.CurrentHealth,
            monster.HealthRevision,
            ownership.Run.TeamScore,
            ownership.Run.LastObservedAt,
            ownership.Mechanics.LastObservedAt);
    }

    private static (
        uint Health,
        ulong HealthRevision,
        int TeamScore,
        DateTimeOffset LastObservedAt,
        int EmittedTicks,
        DateTimeOffset? NextTickAt) PeriodicState(
            Godswar.Server.Game.MapInstance map,
            uint objectId)
    {
        if (!map.TryGetMonsterSnapshot(objectId, out var monster) ||
            !map.TryGetMedusaOwnershipSnapshot(out var ownership))
        {
            throw new InvalidOperationException(
                "Periodic damage fixture state is unavailable.");
        }

        var effect = ownership.Mechanics.Characters
            .Single(character => character.CharacterId == 101)
            .ActiveEffects.Single(active =>
                active.Definition.Kind ==
                    MedusaEncounterEffectKind.Bleed);
        return (
            monster.CurrentHealth,
            monster.HealthRevision,
            ownership.Run.TeamScore,
            ownership.Mechanics.LastObservedAt,
            effect.EmittedPeriodicTicks,
            effect.NextPeriodicTickAt);
    }
}
