using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckPeriodicOwnerCoupling()
    {
        CheckPeriodicObserveRefreshAndAbandon();
        CheckPeriodicPlayerDamageBarrier();
        CheckPeriodicLifeClearBarrier();
        CheckPeriodicDeadlineAndInvariantDisposition();
        CheckPeriodicInvalidOperationsDoNotReserve();
        CheckPeriodicPreparedDefeat();
        CheckPeriodicRegistryLifeAdvanceAsync()
            .GetAwaiter()
            .GetResult();
        CheckPeriodicLiveDeadlineReconciliationAsync()
            .GetAwaiter()
            .GetResult();
    }

    private static void CheckPeriodicObserveRefreshAndAbandon()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var bleed = Binding(bound, "Chrysaor");
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                StartedAt.AddSeconds(1),
                out var applied) &&
            applied.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            "owner periodic fixture applies Bleed");

        var dueAt = StartedAt.AddSeconds(3);
        Check.True(
            map.TryObserveMedusaTime(dueAt, out var observed) &&
            observed.GateOutcome ==
                MedusaOwnedOperationGateOutcome.PeriodicDamageRequired &&
            observed.RunOutcome is null &&
            observed.MechanicsResult is
            {
                Outcome: MedusaMechanicsClockOutcome
                    .PeriodicDamageRequired,
                PeriodicDamage: { } first
            } &&
            first.Identity.DueAt == dueAt,
            "owner observation hands off due work before either clock moves");
        var reservation = observed.MechanicsResult!.Value.PeriodicDamage!;
        AssertCoupledAt(
            map,
            StartedAt.AddSeconds(1),
            "periodic observe barrier");

        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                dueAt,
                out var blockedRefresh) &&
            blockedRefresh.GateOutcome ==
                MedusaOwnedOperationGateOutcome.PeriodicDamageRequired &&
            ReferenceEquals(
                blockedRefresh.MechanicsResult?.PeriodicDamage,
                reservation),
            "equal-time refresh reacquires the existing owner capability");
        Check.True(
            TryCompletePeriodicDamageForProtocolCheck(
                map,
                reservation,
                terminal: false,
                out var completed) &&
            completed == MedusaPeriodicDamageDispositionOutcome.Applied &&
            TryCompletePeriodicDamageForProtocolCheck(
                map,
                reservation,
                terminal: false,
                out var duplicate) &&
            duplicate ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted,
            "owner completion advances exactly once and duplicate is typed");
        AssertCoupledAt(map, dueAt, "periodic completion");

        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                dueAt,
                out var refreshed) &&
            refreshed.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Refreshed,
            "refresh retries after the equal-time tick");

        var abandonDue = dueAt.AddSeconds(2);
        Check.True(
            map.TryAbandonMedusaRun(
                101,
                abandonDue,
                out var blockedAbandon) &&
            blockedAbandon.GateOutcome ==
                MedusaOwnedOperationGateOutcome.PeriodicDamageRequired &&
            blockedAbandon.RunOutcome is null &&
            blockedAbandon.PeriodicDamage is { } abandonTick,
            "abandon exposes an explicit periodic gate and no undefined run outcome");
        _ = TryCompletePeriodicDamageForProtocolCheck(
            map,
            blockedAbandon.PeriodicDamage,
            terminal: false,
            out _);
        Check.True(
            map.TryAbandonMedusaRun(
                101,
                abandonDue,
                out var abandoned) &&
            abandoned.GateOutcome ==
                MedusaOwnedOperationGateOutcome.Delegated &&
            abandoned.RunOutcome == MedusaRunAbandonOutcome.Exited &&
            abandoned.PeriodicDamage is null,
            "abandon retries only after due work and then terminalizes");
        var exited = RequiredOwnership(map);
        Check.True(
            exited.Run.State == MedusaRunState.VoluntarilyExited &&
            exited.Mechanics.Characters.All(static character =>
                character.ActiveEffects.IsEmpty),
            "accepted abandon clears every retained mechanic under the coupled owner");
        AssertCoupledAt(map, abandonDue, "periodic-before-abandon");
    }

    private static void CheckPeriodicPlayerDamageBarrier()
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            "periodic player-damage fixture attaches");
        var bleed = Binding(
            fixture.Map.TryGetMedusaOwnershipSnapshot(out var ownership)
                ? ownership
                : throw new InvalidOperationException(
                    "periodic fixture lost ownership"),
            "Chrysaor");
        _ = fixture.Map.TryCommitOwnerMechanicForInvariantTest(
            101,
            bleed.Identity.ObjectId,
            bleed.Identity.SpawnGeneration,
            StartedAt.AddSeconds(1),
            out _);
        var target = FindMonster(fixture.Map, "Stheno");
        var before = target.CurrentHealth;
        var blocked = CommitTypedDamage(
            fixture.Map,
            target,
            101,
            CombatDamageChannel.Physical,
            damage: 100,
            StartedAt.AddSeconds(3));
        Check.True(
            blocked.Outcome ==
                MedusaPlayerMonsterDamageOutcome
                    .PeriodicDamageHandoffUnavailable &&
            blocked.DamageResult is null &&
            fixture.Map.TryGetMonsterSnapshot(target.ObjectId, out var after) &&
            after.CurrentHealth == before,
            "nonlethal player damage cannot overtake a due tick");

        Check.True(
            fixture.Map.TryObserveMedusaTime(
                StartedAt.AddSeconds(3),
                out var due) &&
            due.MechanicsResult?.PeriodicDamage is not null,
            "owner exposes the blocked exact tick");
        _ = TryCompletePeriodicDamageForProtocolCheck(
            fixture.Map,
            due.MechanicsResult?.PeriodicDamage,
            terminal: true,
            out var terminal);
        Check.True(
            terminal == MedusaPeriodicDamageDispositionOutcome.Terminal,
            "owner can terminal-reconcile the blocked exact tick");
        var retryTarget = fixture.Map.TryGetMonsterSnapshot(
            target.ObjectId,
            out var currentTarget)
            ? currentTarget
            : throw new InvalidOperationException(
                "periodic retry target disappeared");
        var retried = CommitTypedDamage(
            fixture.Map,
            retryTarget,
            101,
            CombatDamageChannel.Physical,
            damage: 100,
            StartedAt.AddSeconds(3));
        Check.True(
            retried is
            {
                Applied: true,
                DamageResult.HealthMutation: not null
            } &&
            retried.DamageResult.BeforeHealth == before &&
            retried.DamageResult.AfterHealth ==
                before - retried.Resolution.Damage,
            "same-timestamp action retry commits HP exactly once");
        AssertCoupledAt(
            fixture.Map,
            StartedAt.AddSeconds(3),
            "periodic-before-player-damage");
    }

    private static void CheckPeriodicLifeClearBarrier()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var session = new ClientSession(new ScriptedLegacyByteTransport());
        try
        {
            var context = CreateAdmittedDamageContext(map, session, 101)
                with
                {
                    Ownership = new(
                        new Guid(
                            "2cc0badd-59f7-4cf4-8b0f-9c7ef043aa77"),
                        Generation: 1)
                };
            map.AddOrUpdate(context);
            var bleed = Binding(bound, "Chrysaor");
            _ = map.TryCommitOwnerMechanicForInvariantTest(
                101,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                StartedAt.AddSeconds(1),
                out _);

            var clearBlocked =
                !map.ClearMedusaCharacterEffectsForLifeGuarded(
                    context,
                    expectedLifeRevision: 0,
                    StartedAt.AddSeconds(3));
            var observed = map.TryObserveMedusaTime(
                StartedAt.AddSeconds(3),
                out var blocked);
            Check.True(
                clearBlocked &&
                observed &&
                blocked.MechanicsResult?.PeriodicDamage is not null,
                "unreserved due work blocks exact-life clear");
            _ = TryCompletePeriodicDamageForProtocolCheck(
                map,
                blocked.MechanicsResult?.PeriodicDamage,
                terminal: true,
                out var terminal);
            Check.True(
                terminal ==
                    MedusaPeriodicDamageDispositionOutcome.Terminal &&
                map.ClearMedusaCharacterEffectsForLifeGuarded(
                    context,
                    expectedLifeRevision: 0,
                    StartedAt.AddSeconds(3)),
                "life clear retries after exact terminal disposition");
            AssertCoupledAt(
                map,
                StartedAt.AddSeconds(3),
                "periodic-before-life-clear");
        }
        finally
        {
            _ = map.Remove(session, out _);
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckPeriodicDeadlineAndInvariantDisposition()
    {
        var map = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var bound = Bind(
            map,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var bleed = Binding(bound, "Chrysaor");
        var stun = Binding(bound, "E1-Elite");
        _ = map.TryCommitOwnerMechanicForInvariantTest(
            101,
            bleed.Identity.ObjectId,
            bleed.Identity.SpawnGeneration,
            bound.Run.Deadline.AddSeconds(-5),
            out _);

        var postDeadlineAt = bound.Run.Deadline.AddTicks(1);
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                stun.Identity.ObjectId,
                stun.Identity.SpawnGeneration,
                postDeadlineAt,
                out var first) &&
            first.GateOutcome ==
                MedusaOwnedOperationGateOutcome.PeriodicDamageRequired &&
            first.MechanicsResult?.PeriodicDamage?.Identity.DueAt ==
                bound.Run.Deadline.AddSeconds(-3),
            "fixed postdeadline hit hands off the earliest overdue predeadline tick");
        _ = TryCompletePeriodicDamageForProtocolCheck(
            map,
            first.MechanicsResult?.PeriodicDamage,
            terminal: false,
            out _);
        _ = map.TryCommitOwnerMechanicForInvariantTest(
            101,
            stun.Identity.ObjectId,
            stun.Identity.SpawnGeneration,
            postDeadlineAt,
            out var second);
        Check.True(
            second.GateOutcome ==
                MedusaOwnedOperationGateOutcome.PeriodicDamageRequired &&
            second.MechanicsResult?.PeriodicDamage?.Identity.DueAt ==
                bound.Run.Deadline.AddSeconds(-1),
            "the same postdeadline action time hands off remaining predeadline work");
        _ = TryCompletePeriodicDamageForProtocolCheck(
            map,
            second.MechanicsResult?.PeriodicDamage,
            terminal: false,
            out _);
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                101,
                stun.Identity.ObjectId,
                stun.Identity.SpawnGeneration,
                postDeadlineAt,
                out var terminal) &&
            terminal.GateOutcome ==
                MedusaOwnedOperationGateOutcome.TimedOut &&
            RequiredOwnership(map).Run.State == MedusaRunState.TimedOut &&
            RequiredOwnership(map).Mechanics.Characters.All(
                static character => character.ActiveEffects.IsEmpty),
            "the exact postdeadline action retry terminalizes after all older work drains");
        AssertCoupledAt(map, postDeadlineAt, "periodic postdeadline drain");

        var boundaryMap = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var boundaryBound = Bind(
            boundaryMap,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var boundaryStun = Binding(boundaryBound, "E1-Elite");
        Check.True(
            boundaryMap.TryCommitOwnerMechanicForInvariantTest(
                101,
                boundaryStun.Identity.ObjectId,
                boundaryStun.Identity.SpawnGeneration,
                boundaryBound.Run.Deadline.AddTicks(-1),
                out var beforeBoundary) &&
            beforeBoundary.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied &&
            boundaryMap.TryCommitOwnerMechanicForInvariantTest(
                101,
                boundaryStun.Identity.ObjectId,
                boundaryStun.Identity.SpawnGeneration,
                boundaryBound.Run.Deadline,
                out var boundary) &&
            boundary.GateOutcome ==
                MedusaOwnedOperationGateOutcome
                    .DeadlineBoundaryUnresolved &&
            RequiredOwnership(boundaryMap).Run.State ==
                MedusaRunState.Active,
            "exact Deadline advances both clocks but creates no equality tick or terminal action");
        AssertCoupledAt(
            boundaryMap,
            boundaryBound.Run.Deadline,
            "exact periodic deadline boundary");

        var corrupt = CreateMap(MedusaEncounterDifficulty.Enhanced);
        var corruptBound = Bind(
            corrupt,
            MedusaEncounterDifficulty.Enhanced,
            characters: [101]).Snapshot!;
        var corruptBleed = Binding(corruptBound, "Chrysaor");
        _ = corrupt.TryCommitOwnerMechanicForInvariantTest(
            101,
            corruptBleed.Identity.ObjectId,
            corruptBleed.Identity.SpawnGeneration,
            StartedAt.AddSeconds(1),
            out _);
        _ = corrupt.TryObserveMedusaTime(
            StartedAt.AddSeconds(3),
            out var corruptDue);
        var owner = typeof(MapInstance).GetField(
                "_medusaInstanceOwner",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(corrupt) ?? throw new InvalidOperationException(
                "periodic invariant fixture lost owner");
        var run = owner.GetType().GetField(
                "_run",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(owner) as MedusaRunRuntime ??
            throw new InvalidOperationException(
                "periodic invariant fixture lost run");
        _ = run.ObserveTime(StartedAt.AddSeconds(4));
        Check.True(
            TryCompletePeriodicDamageForProtocolCheck(
                corrupt,
                corruptDue.MechanicsResult?.PeriodicDamage,
                terminal: false,
                out var invariant) &&
            invariant ==
                MedusaPeriodicDamageDispositionOutcome.InvariantFault,
            "post-HP owner invariant fault consumes the marker without throwing");
    }
}
