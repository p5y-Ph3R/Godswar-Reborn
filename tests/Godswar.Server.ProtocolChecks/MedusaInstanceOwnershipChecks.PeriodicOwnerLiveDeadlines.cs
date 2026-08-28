using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckPeriodicLiveDeadlineReconciliationAsync()
    {
        await CheckPeriodicTerminalRosterAuthorityAsync();
        await CheckPeriodicPlayerOwnerAcceptedWorkAsync();
        await CheckPeriodicRegistryPlayerDeadlineAsync();
#if DEBUG
        await CheckPeriodicRegistryMonsterDeadlineAsync();
#endif
    }

    private static async Task CheckPeriodicPlayerOwnerAcceptedWorkAsync()
    {
        await using var fixture = await MonsterPlayerHitFixture.CreateAsync(
            "E1-Elite");
        Check.True(
            fixture.Registry.TryCapturePlayerMonsterTarget(
                fixture.Socket.Session,
                mapId: 200,
                fixture.Source.ObjectId,
                out var captured,
                out var authority),
            "accepted player-owner fixture captures exact current authority");
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var blocker = fixture.Runtime.Owner.TrySubmit(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "player owner blocker was not released");
            }

            return true;
        });
        var blockerCompletion = blocker.RequireCompletion();
        Task<(bool Applied, MedusaPlayerMonsterDamageCommit Commit)>?
            commitTask = null;
        Task<bool>? removeTask = null;
        try
        {
            Check.True(
                entered.Wait(TimeSpan.FromSeconds(3)),
                "accepted player-owner fixture starts its finite blocker");
            var committedAt = RequiredOwnership(fixture.Map)
                .Run.LastObservedAt.AddTicks(1);
            commitTask = Task.Run(() =>
            {
                var applied = fixture.Registry
                    .TryCommitPlayerMonsterDamageGuarded(
                        fixture.Socket.Session,
                        mapId: 200,
                        captured.ObjectId,
                        captured.RuntimeInstanceId,
                        fixture.Character.Id,
                        captured.SpawnGeneration,
                        captured.HealthRevision,
                        authority,
                        committedAt,
                        Resolution(
                            CombatDamageChannel.Physical,
                            damage: 1),
                        out var commit);
                return (Applied: applied, Commit: commit);
            });
            Check.True(
                SpinWait.SpinUntil(
                    () => fixture.Runtime.Owner.GetSnapshot().Queued >= 1,
                    TimeSpan.FromSeconds(3)),
                "player damage is accepted behind the owner blocker");
            var currentOwnership = fixture.Ownership;
            var removeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            removeTask = Task.Run(() =>
            {
                removeStarted.TrySetResult();
                return fixture.Registry.Remove(
                    fixture.Socket.Session,
                    currentOwnership);
            });
            await removeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Check.True(
                !commitTask.IsCompleted && !removeTask.IsCompleted,
                "accepted player damage outlives the 100ms invocation timeout while exact egress waits");

            release.Set();
            var completed = await commitTask.WaitAsync(
                TimeSpan.FromSeconds(3));
            var removed = await removeTask.WaitAsync(
                TimeSpan.FromSeconds(3));
            var after = RequiredMonster(
                fixture.Map,
                captured.ObjectId);
            Check.True(
                completed.Applied &&
                completed.Commit.Outcome ==
                    MedusaPlayerMonsterDamageOutcome.AppliedMedusa &&
                completed.Commit.DamageResult is
                {
                    Killed: false,
                    HealthMutation: not null
                } damage &&
                damage.BeforeHealth == captured.CurrentHealth &&
                damage.AfterHealth + 1 == damage.BeforeHealth &&
                after.HealthRevision == captured.HealthRevision + 1 &&
                after.CurrentHealth == captured.CurrentHealth - 1 &&
                removed,
                "accepted player damage commits exactly once before contending egress");
        }
        finally
        {
            release.Set();
            await AwaitPeriodicOwnerCleanupAsync(commitTask);
            await AwaitPeriodicOwnerCleanupAsync(removeTask);
            _ = await blockerCompletion.WaitAsync(
                TimeSpan.FromSeconds(3));
        }
    }

    private static async Task CheckPeriodicRegistryPlayerDeadlineAsync()
    {
        await using var fixture = await MonsterPlayerHitFixture.CreateAsync(
            "E1-Elite");
        await DrainMedusaPacketsAsync(fixture.Socket);
        var deadline = PrepareAndDrainDeadlineEffects(
            fixture.Map,
            fixture.Character.Id);
        var target = FindMonster(fixture.Map, "Stheno");
        Check.True(
            fixture.Registry.TryCapturePlayerMonsterTarget(
                fixture.Socket.Session,
                mapId: 200,
                target.ObjectId,
                out var captured,
                out var authority),
            "registry player deadline fixture captures exact current authority");
        var beforeHealth = captured.CurrentHealth;
        var boundaryApplied = fixture.Registry
            .TryCommitPlayerMonsterDamageGuarded(
                fixture.Socket.Session,
                mapId: 200,
                captured.ObjectId,
                captured.RuntimeInstanceId,
                fixture.Character.Id,
                captured.SpawnGeneration,
                captured.HealthRevision,
                authority,
                deadline,
                Resolution(CombatDamageChannel.Physical, damage: 1),
                out var boundary);
        var boundaryOwner = RequiredOwnership(fixture.Map);
        Check.True(
            !boundaryApplied &&
            boundary.Outcome ==
                MedusaPlayerMonsterDamageOutcome
                    .DeadlineBoundaryUnresolved &&
            RequiredMonster(fixture.Map, captured.ObjectId).CurrentHealth ==
                beforeHealth &&
            boundaryOwner.Run.State == MedusaRunState.Active &&
            boundaryOwner.Run.LastObservedAt == deadline &&
            boundaryOwner.Mechanics.LastObservedAt == deadline,
            "real registry player damage reconciles exact Deadline without HP or terminalizing");

        Check.True(
            fixture.Registry.TryCapturePlayerMonsterTarget(
                fixture.Socket.Session,
                mapId: 200,
                target.ObjectId,
                out captured,
                out authority),
            "registry player timeout fixture refreshes exact target authority");
        var timeoutAt = deadline.AddTicks(1);
        var timeoutApplied = fixture.Registry
            .TryCommitPlayerMonsterDamageGuarded(
                fixture.Socket.Session,
                mapId: 200,
                captured.ObjectId,
                captured.RuntimeInstanceId,
                fixture.Character.Id,
                captured.SpawnGeneration,
                captured.HealthRevision,
                authority,
                timeoutAt,
                Resolution(CombatDamageChannel.Physical, damage: 1),
                out var timedOut);
        var terminal = RequiredOwnership(fixture.Map);
        Check.True(
            !timeoutApplied &&
            timedOut.Outcome ==
                MedusaPlayerMonsterDamageOutcome.TimedOut &&
            RequiredMonster(fixture.Map, captured.ObjectId).CurrentHealth ==
                beforeHealth &&
            terminal.Run.State == MedusaRunState.TimedOut &&
            terminal.Run.LastObservedAt == timeoutAt &&
            terminal.Mechanics.LastObservedAt == timeoutAt &&
            terminal.Mechanics.Characters.All(static character =>
                character.ActiveEffects.IsEmpty),
            "real registry player timeout terminalizes both owners without HP");
        await AssertExactTerminalClearOnlyAsync(
            fixture.Socket,
            MedusaHandlerLocalObjectId);
    }

#if DEBUG
    private static async Task CheckPeriodicRegistryMonsterDeadlineAsync()
    {
        await using var fixture = await MonsterPlayerHitFixture.CreateAsync(
            "E1-Elite");
        await DrainMedusaPacketsAsync(fixture.Socket);
        var deadline = PrepareAndDrainDeadlineEffects(
            fixture.Map,
            fixture.Character.Id);
        var hook = typeof(GameSessionRegistry).GetField(
                "_protocolCheckMonsterAttackResolvedAt",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "monster deadline protocol hook is unavailable");
        var beforeHealth = fixture.Character.CurrentHp;
        var beforeVitals = fixture.Character.VitalsRevision;
        using var ownerBlockerEntered =
            new ManualResetEventSlim(false);
        using var ownerBlockerRelease =
            new ManualResetEventSlim(false);
        Task<bool>? ownerBlockerCompletion = null;
        Task? timeoutAttack = null;
        try
        {
            hook.SetValue(
                fixture.Registry,
                new Func<DateTimeOffset, DateTimeOffset>(_ => deadline));
            var boundaryEvent = fixture.FindEvent(
                9_310_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(boundaryEvent));
            var boundary = RequiredOwnership(fixture.Map);
            Check.True(
                fixture.Character.CurrentHp == beforeHealth &&
                fixture.Character.VitalsRevision == beforeVitals &&
                boundary.Run.State == MedusaRunState.Active &&
                boundary.Run.LastObservedAt == deadline &&
                boundary.Mechanics.LastObservedAt == deadline,
                "actual monster transaction reconciles exact Deadline before rejecting its hit");

            var timeoutAt = deadline.AddTicks(1);
            hook.SetValue(
                fixture.Registry,
                new Func<DateTimeOffset, DateTimeOffset>(_ =>
                {
                    var blocker = fixture.Runtime.Owner.TrySubmit(_ =>
                    {
                        ownerBlockerEntered.Set();
                        if (!ownerBlockerRelease.Wait(
                                TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException(
                                "monster owner blocker was not released");
                        }

                        return true;
                    });
                    ownerBlockerCompletion = blocker.RequireCompletion();
                    if (!ownerBlockerEntered.Wait(
                            TimeSpan.FromSeconds(3)))
                    {
                        throw new InvalidOperationException(
                            "monster capture owner blocker did not start");
                    }
                    return timeoutAt;
                }));
            var timeoutEvent = fixture.FindEvent(
                boundaryEvent + 1,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            timeoutAttack = Task.Run(() => fixture.AttackAsync(
                fixture.CreateAttack(timeoutEvent)));
            Check.True(
                SpinWait.SpinUntil(
                    () => fixture.Runtime.Owner.GetSnapshot().Queued >= 1,
                    TimeSpan.FromSeconds(3)),
                "monster timeout capture is accepted behind the owner blocker");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Check.True(
                !timeoutAttack.IsCompleted,
                "accepted monster capture outlives the 100ms invocation timeout");
            ownerBlockerRelease.Set();
            await timeoutAttack.WaitAsync(TimeSpan.FromSeconds(3));
            var terminal = RequiredOwnership(fixture.Map);
            Check.True(
                fixture.Character.CurrentHp == beforeHealth &&
                fixture.Character.VitalsRevision == beforeVitals &&
                terminal.Run.State == MedusaRunState.TimedOut &&
                terminal.Run.LastObservedAt == timeoutAt &&
                terminal.Mechanics.LastObservedAt == timeoutAt &&
                terminal.Mechanics.Characters.All(static character =>
                    character.ActiveEffects.IsEmpty),
                "actual monster transaction carries timeout without HP and terminalizes both owners");
            await AssertExactTerminalClearOnlyAsync(
                fixture.Socket,
                MedusaHandlerLocalObjectId);
        }
        finally
        {
            ownerBlockerRelease.Set();
            hook.SetValue(fixture.Registry, null);
            await AwaitPeriodicOwnerCleanupAsync(timeoutAttack);
            if (ownerBlockerCompletion is not null)
            {
                _ = await ownerBlockerCompletion.WaitAsync(
                    TimeSpan.FromSeconds(3));
            }
        }
    }
#endif

    private static async Task AwaitPeriodicOwnerCleanupAsync(
        Task? task)
    {
        if (task is null || task.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // The primary test assertion owns the failure; cleanup only
            // prevents accepted owner work from being orphaned.
        }
    }

    private static DateTimeOffset PrepareAndDrainDeadlineEffects(
        MapInstance map,
        int characterId)
    {
        var ownership = RequiredOwnership(map);
        var deadline = ownership.Run.Deadline;
        var bleed = Binding(ownership, "Chrysaor");
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                characterId,
                bleed.Identity.ObjectId,
                bleed.Identity.SpawnGeneration,
                deadline.AddSeconds(-5),
                out var applied) &&
            applied.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            "deadline fixture applies Bleed before its boundary");

        for (var index = 0; index < 2; index++)
        {
            Check.True(
                map.TryObserveMedusaTime(deadline, out var due) &&
                due.GateOutcome ==
                    MedusaOwnedOperationGateOutcome
                        .PeriodicDamageRequired &&
                due.MechanicsResult?.PeriodicDamage is { } reservation &&
                reservation.Identity.DueAt < deadline &&
                TryCompletePeriodicDamageForProtocolCheck(
                    map,
                    reservation,
                    terminal: false,
                    out var disposition) &&
                disposition ==
                    MedusaPeriodicDamageDispositionOutcome.Applied,
                "deadline fixture drains each ordered predeadline Bleed unit");
        }

        var current = RequiredOwnership(map);
        var stun = Binding(current, "E1-Elite");
        Check.True(
            map.TryCommitOwnerMechanicForInvariantTest(
                characterId,
                stun.Identity.ObjectId,
                stun.Identity.SpawnGeneration,
                deadline.AddSeconds(-1),
                out var stunned) &&
            stunned.MechanicsResult?.Outcome ==
                MedusaMechanicHitOutcome.Applied,
            "deadline fixture retains a current projectable effect for terminal clear");
        return deadline;
    }

    private static async Task AssertExactTerminalClearOnlyAsync(
        RuntimePolicySessionSocket socket,
        uint expectedObjectId)
    {
        var packet = await socket.ReadPacketAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Check.True(
            MedusaPacketOpcode(packet) == MedusaStatusOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) ==
                expectedObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 0 &&
            socket.Available == 0,
            "timeout publication is exactly one empty current 10167 with no combat prefix or trailing packet");
    }
}
