using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckPeriodicInvalidOperationsDoNotReserve()
    {
        CheckForeignOwnerOperationsDoNotReserve();
        CheckStalePlayerDamageDoesNotReserve();
        CheckDeadPlayerDamageTargetDoesNotReserve();
    }

    private static void CheckForeignOwnerOperationsDoNotReserve()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var ownership = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var bleed = Binding(ownership, "Chrysaor");
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                StartedAt.AddSeconds(1),
                out var applied) &&
            applied.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            "foreign preflight fixture applies Bleed through the owner");
        var source = Binding(ownership, "Stheno");

        Check.True(
            map.TryAbandonMedusaRun(
                requestedByCharacterId: 999,
                StartedAt.AddSeconds(3),
                out var foreignAbandon) &&
            foreignAbandon.RunOutcome ==
                MedusaRunAbandonOutcome.CharacterNotAdmitted &&
            foreignAbandon.PeriodicDamage is null,
            "foreign abandon does not expose due work");
        var foreignDefeat = InvokeOwnerDefeatForInvariantTest(
            map,
            characterId: 999,
            source.Identity.ObjectId,
            source.Identity.SpawnGeneration,
            StartedAt.AddSeconds(3));
        Check.True(
            foreignDefeat.Claim?.Outcome ==
                MedusaDefeatClaimOutcome.CharacterNotAdmitted &&
            foreignDefeat.PeriodicDamage is null,
            "foreign defeat does not expose due work");
        AssertUnreservedBleedAt(
            map,
            expectedClock: StartedAt.AddSeconds(1),
            expectedDueAt: StartedAt.AddSeconds(3),
            "foreign identities");

        var mechanics = RequiredOwnerMechanicsForInvariantTest(map);
        var retired = mechanics.RetireMonster(
            source.Identity.ObjectId,
            source.Identity.SpawnGeneration,
            StartedAt.AddSeconds(1));
        var beforeInvalidRetire = map.TryGetMedusaOwnershipSnapshot(
            out var beforeSnapshot)
            ? beforeSnapshot
            : throw new InvalidOperationException(
                "invalid-retire fixture lost owner");
        var invalidRetireDefeat = InvokeOwnerDefeatForInvariantTest(
            map,
            101,
            source.Identity.ObjectId,
            source.Identity.SpawnGeneration,
            StartedAt.AddSeconds(3));
        Check.True(
            retired.Outcome ==
                MedusaMechanicSourceRetireOutcome.Retired &&
            invalidRetireDefeat.Claim is null &&
            invalidRetireDefeat.SourceRetirement?.Outcome ==
                MedusaMechanicSourceRetireOutcome.AlreadyRetired &&
            map.TryGetMedusaOwnershipSnapshot(out var after) &&
            after.Run.TeamScore == beforeInvalidRetire.Run.TeamScore &&
            after.Run.LastObservedAt ==
                beforeInvalidRetire.Run.LastObservedAt &&
            after.Mechanics.LastObservedAt ==
                beforeInvalidRetire.Mechanics.LastObservedAt &&
            after.Mechanics.OutstandingPeriodicDamage is null,
            "mechanics-side defeat rejection cannot score or reserve a tick");

        Check.True(
            map.TryObserveMedusaTime(
                StartedAt.AddSeconds(3),
                out var due) &&
            due.MechanicsResult?.PeriodicDamage is not null,
            "valid observation reserves the exact pending capability");
        var pending = due.MechanicsResult!.Value.PeriodicDamage!;
        Check.True(
            map.TryObserveMedusaTime(
                StartedAt.AddSeconds(0),
                out var backward) &&
            backward.GateOutcome ==
                MedusaOwnedOperationGateOutcome.TimestampMovedBackward &&
            backward.MechanicsResult is null &&
            mechanics.RetireMonster(
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                StartedAt).Outcome ==
                MedusaMechanicSourceRetireOutcome.TimestampMovedBackward &&
            map.TryObserveMedusaTime(
                StartedAt.AddSeconds(3),
                out var reacquired) &&
            ReferenceEquals(
                reacquired.MechanicsResult?.PeriodicDamage,
                pending),
            "invalid backward observation and retirement hide but do not strand the pending capability");
        _ = TryCompletePeriodicDamageForProtocolCheck(
            map,
            pending,
            terminal: true,
            out _);
    }

    private static void CheckStalePlayerDamageDoesNotReserve()
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            "stale due-action fixture attaches");
        var ownership = fixture.Map.TryGetMedusaOwnershipSnapshot(out var bound)
            ? bound
            : throw new InvalidOperationException(
                "stale due-action fixture lost owner");
        var bleed = Binding(ownership, "Chrysaor");
        _ = fixture.Map.TryCommitOwnerMechanicForInvariantTest(
            101,
            bleed.Identity.ObjectId,
            bleed.Identity.SpawnGeneration,
            StartedAt.AddSeconds(1),
            out _);
        var target = FindMonster(fixture.Map, "Stheno");
        var staleHealth = CommitTypedDamageWithExpectedIdentity(
            fixture.Map,
            target,
            sessionCharacterId: 101,
            attackerCharacterId: 101,
            target.SpawnGeneration,
            checked(target.HealthRevision + 1),
            StartedAt.AddSeconds(3),
            Resolution(CombatDamageChannel.Physical, 100));
        var foreign = CommitTypedDamageWithExpectedIdentity(
            fixture.Map,
            target,
            sessionCharacterId: 101,
            attackerCharacterId: 999,
            target.SpawnGeneration,
            target.HealthRevision,
            StartedAt.AddSeconds(3),
            Resolution(CombatDamageChannel.Physical, 100));
        Check.True(
            staleHealth.Outcome ==
                MedusaPlayerMonsterDamageOutcome.StaleHealthRevision &&
            foreign.Outcome ==
                MedusaPlayerMonsterDamageOutcome
                    .CurrentMembershipRequired,
            "stale and foreign due-time player damage reject first");
        AssertUnreservedBleedAt(
            fixture.Map,
            StartedAt.AddSeconds(1),
            StartedAt.AddSeconds(3),
            "stale player damage");
    }

    private static void CheckDeadPlayerDamageTargetDoesNotReserve()
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            "dead due-action fixture attaches");
        var target = FindMonster(fixture.Map, "Stheno");
        var killed = CommitTypedDamage(
            fixture.Map,
            target,
            101,
            CombatDamageChannel.Physical,
            uint.MaxValue,
            StartedAt.AddSeconds(1));
        var ownership = fixture.Map.TryGetMedusaOwnershipSnapshot(out var bound)
            ? bound
            : throw new InvalidOperationException(
                "dead due-action fixture lost owner");
        var bleed = Binding(ownership, "Chrysaor");
        _ = fixture.Map.TryCommitOwnerMechanicForInvariantTest(
            101,
            bleed.Identity.ObjectId,
            bleed.Identity.SpawnGeneration,
            StartedAt.AddSeconds(1),
            out _);
        var dead = fixture.Map.TryGetMonsterSnapshot(
            target.ObjectId,
            out var current)
            ? current
            : throw new InvalidOperationException(
                "dead due-action target disappeared");
        var rejected = CommitTypedDamage(
            fixture.Map,
            dead,
            101,
            CombatDamageChannel.Physical,
            100,
            StartedAt.AddSeconds(3));
        Check.True(
            killed.DamageResult?.Killed == true &&
            rejected.Outcome ==
                MedusaPlayerMonsterDamageOutcome.RuntimeRejected,
            "dead due-time target rejects before owner observation");
        AssertUnreservedBleedAt(
            fixture.Map,
            StartedAt.AddSeconds(1),
            StartedAt.AddSeconds(3),
            "dead player-damage target");
    }

    private static void AssertUnreservedBleedAt(
        Godswar.Server.Game.MapInstance map,
        DateTimeOffset expectedClock,
        DateTimeOffset expectedDueAt,
        string scenario)
    {
        Check.True(
            map.TryGetMedusaOwnershipSnapshot(out var ownership) &&
            ownership.Run.LastObservedAt == expectedClock &&
            ownership.Mechanics.LastObservedAt == expectedClock &&
            ownership.Mechanics.OutstandingPeriodicDamage is null &&
            ownership.Mechanics.Characters
                .SelectMany(static character => character.ActiveEffects)
                .Single(effect => effect.Definition.Kind ==
                    MedusaEncounterEffectKind.Bleed)
                .NextPeriodicTickAt == expectedDueAt,
            $"{scenario} leaves due work unreserved and clocks unchanged");
    }
}
