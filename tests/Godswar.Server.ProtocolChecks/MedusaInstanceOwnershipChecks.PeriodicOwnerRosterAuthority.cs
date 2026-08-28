using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckPeriodicTerminalRosterAuthorityAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        _ = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);

        try
        {
            await DrainMedusaPacketsAsync(fixture.Socket);
            await DrainMedusaPacketsAsync(observerSocket);
            Check.True(
                fixture.Registry.TryGetPlayerLifeRevision(
                    fixture.Socket.Session,
                    out var targetLife) &&
                fixture.Registry.TryGetPlayerLifeRevision(
                    observerSocket.Session,
                    out _),
                "terminal-roster fixture begins with two exact life fences");

            fixture.Registry.RemovePlayerStatusState(
                observerSocket.Session);
            await DrainMedusaPacketsAsync(fixture.Socket);
            await DrainMedusaPacketsAsync(observerSocket);
            Check.True(
                fixture.Registry.TryGetPlayerLifeRevision(
                    fixture.Socket.Session,
                    out var retainedTargetLife) &&
                retainedTargetLife == targetLife &&
                !fixture.Registry.TryGetPlayerLifeRevision(
                    observerSocket.Session,
                    out _),
                "terminal-roster fixture removes only the ready observer life fence");

            CheckPeriodicPlayerRosterAuthority(
                fixture,
                observerSocket,
                targetLife);
            await CheckPeriodicMonsterRosterAuthorityAsync(
                fixture,
                observerSocket,
                targetLife);
        }
        finally
        {
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static void CheckPeriodicPlayerRosterAuthority(
        MonsterPlayerHitFixture fixture,
        RuntimePolicySessionSocket observerSocket,
        long targetLife)
    {
        Check.True(
            fixture.Registry.TryCapturePlayerMonsterTarget(
                fixture.Socket.Session,
                mapId: 200,
                fixture.Source.ObjectId,
                out var captured,
                out var authority) &&
            authority.LifeRevision == targetLife,
            "player roster-authority action has an exact current attacker and target");
        var beforeOwner = RequiredOwnership(fixture.Map);
        var beforeMonster = RequiredMonster(
            fixture.Map,
            captured.ObjectId);
        var beforeReplay = fixture.Registry
            .GetPlayerVitalsDamageEcsDiagnostics(
                fixture.Socket.Session);

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
                beforeOwner.Run.LastObservedAt.AddTicks(1),
                Resolution(CombatDamageChannel.Physical, damage: 1),
                out var commit);
        var afterMonster = RequiredMonster(
            fixture.Map,
            captured.ObjectId);

        Check.True(
            !applied &&
            commit == default &&
            beforeMonster == afterMonster &&
            OwnershipSnapshotsValueEqual(
                beforeOwner,
                RequiredOwnership(fixture.Map)) &&
            Equals(
                beforeReplay,
                fixture.Registry.GetPlayerVitalsDamageEcsDiagnostics(
                    fixture.Socket.Session)) &&
            fixture.Registry.TryGetPlayerLifeRevision(
                fixture.Socket.Session,
                out var currentTargetLife) &&
            currentTargetLife == targetLife &&
            !fixture.Registry.TryGetPlayerLifeRevision(
                observerSocket.Session,
                out _) &&
            fixture.Socket.Available == 0 &&
            observerSocket.Available == 0,
            "player damage rejects a missing ready observer life before owner submission, HP, clocks, replay, or packets");
    }

    private static async Task CheckPeriodicMonsterRosterAuthorityAsync(
        MonsterPlayerHitFixture fixture,
        RuntimePolicySessionSocket observerSocket,
        long targetLife)
    {
        var eventId = fixture.FindEvent(
            9_320_000,
            static resolution => resolution.Hit &&
                resolution.Damage > 0);
        var attack = fixture.CreateAttack(eventId);
        var beforeOwner = RequiredOwnership(fixture.Map);
        var beforeSource = RequiredMonster(
            fixture.Map,
            fixture.Source.ObjectId);
        var beforeReplay = fixture.Registry
            .GetPlayerVitalsDamageEcsDiagnostics(
                fixture.Socket.Session);
        var beforeOwnerReplay = ReadPeriodicOwnerReplayCount(
            fixture.Map);
        var beforeHealth = fixture.Character.CurrentHp;
        var beforeVitals = fixture.Character.VitalsRevision;

        var rejected = await fixture.AttackAsync(attack);

        Check.True(
            rejected.BeforeHealth == beforeHealth &&
            rejected.AfterHealth == beforeHealth &&
            rejected.BeforeVitalsRevision == beforeVitals &&
            rejected.AfterVitalsRevision == beforeVitals &&
            beforeSource == RequiredMonster(
                fixture.Map,
                fixture.Source.ObjectId) &&
            OwnershipSnapshotsValueEqual(
                beforeOwner,
                RequiredOwnership(fixture.Map)) &&
            Equals(
                beforeReplay,
                fixture.Registry.GetPlayerVitalsDamageEcsDiagnostics(
                    fixture.Socket.Session)) &&
            ReadPeriodicOwnerReplayCount(fixture.Map) ==
                beforeOwnerReplay &&
            fixture.Registry.TryGetPlayerLifeRevision(
                fixture.Socket.Session,
                out var currentTargetLife) &&
            currentTargetLife == targetLife &&
            !fixture.Registry.TryGetPlayerLifeRevision(
                observerSocket.Session,
                out _) &&
            fixture.Socket.Available == 0 &&
            observerSocket.Available == 0,
            "monster damage rejects a missing ready observer life before owner capture, HP, clocks, replay, or packets");
    }

    private static int ReadPeriodicOwnerReplayCount(MapInstance map)
    {
        var owner = typeof(MapInstance).GetField(
                "_medusaInstanceOwner",
                BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(map) ??
            throw new InvalidOperationException(
                "periodic roster owner diagnostics are unavailable");
        var replay = owner.GetType().GetField(
                "_monsterPlayerHitReplay",
                BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(owner) ??
            throw new InvalidOperationException(
                "periodic roster replay diagnostics are unavailable");
        return (int)(replay.GetType().GetProperty("Count")?
            .GetValue(replay) ??
            throw new InvalidOperationException(
                "periodic roster replay count is unavailable"));
    }
}
