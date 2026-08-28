using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckCurrentMembershipCommitRaceAsync()
    {
        var fixture = CreateAttachmentFixture();
        Check.True(AttachAuthored(fixture).IsAttached,
            "membership-race fixture attaches");
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var context = CreateAdmittedDamageContext(
            fixture.Map,
            socket.Session,
            characterId: 101);
        fixture.Map.AddOrUpdate(context);
        var target = FindMonster(fixture.Map, "E13-Elite");
        var authority = new PlayerMonsterCombatAuthority(
            context.WorldInstanceId,
            context.WorldRevision,
            context.Ownership,
            LifeRevision: 0,
            context.WorldMembershipEpoch);
        var source = Resolution(
            CombatDamageChannel.Physical,
            damage: 1);
        var monsterGate = RequiredMapGate(
            fixture.Map,
            "_monsterRuntimeGate");
        var membershipGate = RequiredMapGate(
            fixture.Map,
            "_membershipGate");
        Task<MedusaPlayerMonsterDamageCommit>? damageTask = null;
        Task<bool>? egressTask = null;

        Monitor.Enter(monsterGate);
        try
        {
            damageTask = Task.Run(() =>
                fixture.Map
                    .TryCommitPlayerMonsterDamageForSessionGuarded(
                        socket.Session,
                        target.ObjectId,
                        target.RuntimeInstanceId,
                        context.CharacterId,
                        target.SpawnGeneration,
                        target.HealthRevision,
                        authority,
                        StartedAt.AddSeconds(1),
                        source));
            Check.True(
                SpinWait.SpinUntil(
                    () => GateIsHeldByAnotherThread(membershipGate),
                    TimeSpan.FromSeconds(2)),
                "damage reaches the exact-membership gate before the race is released");

            using var egressStarted = new ManualResetEventSlim();
            egressTask = Task.Run(() =>
            {
                egressStarted.Set();
                return fixture.Map.Remove(socket.Session, out _);
            });
            Check.True(
                egressStarted.Wait(TimeSpan.FromSeconds(2)) &&
                !egressTask.Wait(TimeSpan.FromMilliseconds(100)),
                "egress cannot overtake a damage commit that already owns current membership");
        }
        finally
        {
            Monitor.Exit(monsterGate);
        }

        var committed = await damageTask!;
        var removed = await egressTask!;
        Check.True(
            committed.Outcome ==
                MedusaPlayerMonsterDamageOutcome.AppliedMedusa &&
            committed.DamageResult is { } damage &&
            damage.BeforeHealth - damage.AfterHealth == 1 &&
            removed,
            "the winning damage transaction commits before the serialized egress");

        var after = fixture.Map.TryGetMonsterSnapshot(
            target.ObjectId,
            out var current)
            ? current
            : throw new InvalidOperationException(
                "Membership-race target disappeared.");
        var rejected = fixture.Map
            .TryCommitPlayerMonsterDamageForSessionGuarded(
                socket.Session,
                after.ObjectId,
                after.RuntimeInstanceId,
                context.CharacterId,
                after.SpawnGeneration,
                after.HealthRevision,
                authority,
                StartedAt.AddSeconds(2),
                source);
        Check.True(
            rejected.Outcome ==
                MedusaPlayerMonsterDamageOutcome
                    .CurrentMembershipRequired &&
            rejected.DamageResult is null &&
            fixture.Map.TryGetMonsterSnapshot(
                after.ObjectId,
                out var unchanged) &&
            unchanged.CurrentHealth == after.CurrentHealth &&
            unchanged.HealthRevision == after.HealthRevision,
            "an exact-session commit after egress rejects before HP mutation");
    }

    private static async Task
        CheckMembershipEpochRejectsRejoinedDamageAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E13-Elite");
        Check.True(
            fixture.Registry.TryCapturePlayerMonsterTarget(
                fixture.Socket.Session,
                fixture.Map.MapId,
                fixture.Source.ObjectId,
                out var target,
                out var captured),
            "registry captures player attack authority at epoch A");
        var original = fixture.Context;
        var originalLife = fixture.Registry.GetPlayerLifeRevision(
            fixture.Socket.Session);
        await fixture.RejoinSameAuthorityAsync();
        var rejoined = fixture.Context;

        var applied = fixture.Registry
            .TryCommitPlayerMonsterDamageGuarded(
                fixture.Socket.Session,
                fixture.Map.MapId,
                target.ObjectId,
                target.RuntimeInstanceId,
                original.CharacterId,
                target.SpawnGeneration,
                target.HealthRevision,
                captured,
                StartedAt.AddSeconds(1),
                Resolution(CombatDamageChannel.Physical, damage: 1),
                out var rejected);

        Check.True(
            !applied &&
            rejected == default &&
            captured.WorldInstanceId == original.WorldInstanceId &&
            captured.WorldRevision == original.WorldRevision &&
            captured.Ownership == original.Ownership &&
            captured.LifeRevision == originalLife &&
            captured.WorldMembershipEpoch ==
                original.WorldMembershipEpoch &&
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session) == originalLife &&
            (rejoined with
            {
                WorldMembershipEpoch = original.WorldMembershipEpoch
            }) == original &&
            rejoined.WorldMembershipEpoch !=
                original.WorldMembershipEpoch &&
            fixture.Map.TryGetMonsterSnapshot(
                target.ObjectId,
                out var unchanged) &&
            unchanged == target,
            "a registry-captured player attack cannot cross a same-session " +
            "same-authority membership epoch replacement");
    }
}
