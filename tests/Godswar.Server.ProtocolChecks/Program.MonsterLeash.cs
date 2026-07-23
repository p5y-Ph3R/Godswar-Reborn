using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static Task CheckMonsterLeashReturnAsync()
    {
        var start = new DateTimeOffset(2026, 5, 12, 18, 0, 0, TimeSpan.FromHours(12));
        var definition = CreateCapturedMonster(
            10014,
            100f,
            50f,
            "A_normal_stub_001",
            tier: 1,
            maximumHealth: 237);
        var target = new MonsterCombatTarget(
            CharacterId: 732,
            X: definition.X + 20f,
            Z: definition.Z,
            IsAlive: true);
        var runtime = new MonsterMapRuntime(0, [definition], start);
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 37,
                attackerCharacterId: target.CharacterId,
                now: start,
                out var hit) &&
            hit.AfterHealth == 200,
            "leash fixture begins with a damaged aggroed monster");

        Check.True(
            MonsterMapRuntime.CombatLeashRadius >=
            MonsterMapRuntime.MaximumRoamRadius * 4,
            "combat chase boundary is substantially larger than idle roaming");

        runtime.Advance(start, [target]);
        var now = start;
        for (var chaseTick = 0; chaseTick < 24; chaseTick++)
        {
            now += MonsterMapRuntime.TickInterval;
            runtime.Advance(now, [target]);
        }

        var chased = runtime.Snapshot().Single();
        var chasedHomeDistance = Math.Sqrt(
            Math.Pow(chased.X - chased.HomeX, 2) +
            Math.Pow(chased.Z - chased.HomeZ, 2));
        Check.True(
            chased.X > chased.HomeX &&
            chasedHomeDistance > MonsterMapRuntime.MaximumRoamRadius &&
            chased.CurrentHealth == 200 &&
            chased.IsAlive &&
            chased.IsSpawned,
            "monster chases well beyond the former eight-unit leash without resetting");

        var escapedTarget = target with
        {
            X = definition.X + MonsterMapRuntime.CombatLeashRadius +
                MonsterMapRuntime.CombatRange + 1f
        };
        now += MonsterMapRuntime.TickInterval;
        var returnStartTick = runtime.Advance(now, [escapedTarget]);
        var returnStart = returnStartTick.Updates.Single(update =>
            update.Kind == MonsterRuntimeUpdateKind.Started);
        Check.Equal(0u, returnStart.MovementMode, "leash return starts a new inward movement leg");
        Check.True(
            returnStart.Monster.IsAlive &&
            returnStart.Monster.IsSpawned &&
            returnStart.Monster.IsMoving &&
            returnStart.Monster.CombatPhase == MonsterCombatPhase.Returning &&
            returnStart.Monster.VelocityX < 0 &&
            returnStart.Monster.CurrentHealth == 200 &&
            returnStart.Monster.SpawnGeneration == 1,
            "crossing the leash keeps the damaged monster visible while it turns home");
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 100,
                attackerCharacterId: target.CharacterId,
                now,
                out var returnHit) &&
            returnHit.BeforeHealth == 200 &&
            returnHit.AfterHealth == 200 &&
            returnHit.Monster.CombatPhase == MonsterCombatPhase.Returning,
            "the visible returning monster is invulnerable");
        Check.True(
            runtime.TryApplyStun(
                definition.ObjectId,
                target.CharacterId,
                TimeSpan.FromSeconds(1),
                now,
                out var returnStun) &&
            !returnStun.Applied &&
            returnStun.Monster.CombatPhase == MonsterCombatPhase.Returning,
            "the visible returning monster rejects control effects");

        var previousHomeDistance = Math.Abs(returnStart.Monster.X - returnStart.Monster.HomeX);
        MonsterRuntimeUpdate? returned = null;
        for (var returnTick = 0; returnTick < 64 && returned is null; returnTick++)
        {
            now += MonsterMapRuntime.TickInterval;
            var tick = runtime.Advance(now, [escapedTarget]);
            returned = tick.Updates.SingleOrDefault(update =>
                update.Kind == MonsterRuntimeUpdateKind.Returned);
            if (returned is null)
            {
                Check.True(
                    tick.Updates.All(update =>
                        update.Kind is not MonsterRuntimeUpdateKind.Attacked and
                            not MonsterRuntimeUpdateKind.Despawned),
                    "return movement neither attacks nor retires before reaching home");
                var snapshot = runtime.Snapshot().Single();
                var homeDistance = Math.Abs(snapshot.X - snapshot.HomeX);
                Check.True(
                    homeDistance <= previousHomeDistance + 0.0001,
                    "every return step moves monotonically toward home");
                previousHomeDistance = homeDistance;
                Check.True(
                    snapshot.IsAlive &&
                    snapshot.IsSpawned &&
                    snapshot.CurrentHealth == 200 &&
                    snapshot.CombatPhase == MonsterCombatPhase.Returning,
                    "the damaged old generation remains visible throughout its return");
            }
            else
            {
                Check.True(
                    tick.Updates.Select(update => update.Kind).SequenceEqual(
                        [MonsterRuntimeUpdateKind.Returned, MonsterRuntimeUpdateKind.Despawned]),
                    "exact-home arrival orders movement-end before retiring the old generation");
            }
        }

        Check.True(returned is not null, "leashed monster reaches its exact home");
        Check.Equal(1u, returned!.MovementEndField ?? 0, "home arrival emits movement completion");
        Check.True(
            returned.Monster.X == returned.Monster.HomeX &&
            returned.Monster.Z == returned.Monster.HomeZ &&
            returned.Monster.IsAlive &&
            returned.Monster.IsSpawned &&
            !returned.Monster.IsMoving &&
            returned.Monster.CurrentHealth == 200 &&
            returned.Monster.CombatPhase == MonsterCombatPhase.AwaitingRetirement &&
            returned.Monster.SpawnGeneration == 1,
            "home arrival preserves the damaged old generation until movement-end is published");
        var retired = runtime.Snapshot().Single();
        Check.True(
            !retired.IsAlive &&
            !retired.IsSpawned &&
            retired.CurrentHealth == 200 &&
            retired.SpawnGeneration == 1,
            "the old generation retires only after its exact-home snapshot is captured");
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 100,
                attackerCharacterId: target.CharacterId,
                now,
                out var retiredHit) &&
            retiredHit.BeforeHealth == retiredHit.AfterHealth,
            "the retired generation cannot receive damage before replacement");

        now += MonsterMapRuntime.TickInterval;
        var respawnTick = runtime.Advance(now, [escapedTarget]);
        var respawned = respawnTick.Updates.Single(update =>
            update.Kind == MonsterRuntimeUpdateKind.Respawned).Monster;
        Check.True(
            respawned.IsAlive &&
            respawned.IsSpawned &&
            !respawned.IsMoving &&
            respawned.CombatPhase == MonsterCombatPhase.None &&
            respawned.CurrentHealth == respawned.MaximumHealth &&
            respawned.X == respawned.HomeX &&
            respawned.Z == respawned.HomeZ &&
            respawned.SpawnGeneration == 2,
            "the following world tick creates a fresh full-health runtime generation at home");
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                now,
                out var replacementHit) &&
            replacementHit.AfterHealth + 1 == replacementHit.BeforeHealth,
            "the replacement is attackable immediately after its respawn tick");

        var queuedAtHomeRuntime = new MonsterMapRuntime(0, [definition], start);
        Check.True(
            queuedAtHomeRuntime.TryApplyDamage(
                definition.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                start,
                out _),
            "at-home return fixture acquires aggro");
        queuedAtHomeRuntime.ClearAggroForCharacter(target.CharacterId, start);
        var queuedAtHomeTick = queuedAtHomeRuntime.Advance(
            start + TimeSpan.FromSeconds(2),
            []);
        Check.True(
            queuedAtHomeTick.Updates.Select(update => update.Kind)
                .SequenceEqual(
                    [MonsterRuntimeUpdateKind.Returned, MonsterRuntimeUpdateKind.Despawned]) &&
            queuedAtHomeRuntime.Snapshot().Single() is
            {
                IsAlive: false,
                IsSpawned: false,
                CombatPhase: MonsterCombatPhase.AwaitingRetirement
            },
            "a queued at-home return orders movement-end before retirement without respawning");
        var queuedRespawnTick = queuedAtHomeRuntime.Advance(
            start + TimeSpan.FromSeconds(2) + MonsterMapRuntime.TickInterval,
            []);
        Check.True(
            queuedRespawnTick.Updates.Single().Kind == MonsterRuntimeUpdateKind.Respawned &&
            queuedAtHomeRuntime.Snapshot().Single() is
            {
                IsAlive: true,
                IsSpawned: true,
                SpawnGeneration: 2
            },
            "queued at-home replacement spawns as a fresh generation one tick later");

        var lostTargetRuntime = new MonsterMapRuntime(0, [definition], start);
        lostTargetRuntime.TryApplyDamage(
            definition.ObjectId,
            damage: 1,
            attackerCharacterId: target.CharacterId,
            now: start,
            out _);
        lostTargetRuntime.Advance(start, [target]);
        lostTargetRuntime.Advance(start + MonsterMapRuntime.TickInterval, [target]);
        var lostTargetTick = lostTargetRuntime.Advance(
            start + (MonsterMapRuntime.TickInterval * 2),
            []);
        Check.True(
            lostTargetTick.Updates.Any(update =>
                update.Kind == MonsterRuntimeUpdateKind.Started &&
                update.Monster.IsSpawned &&
                update.Monster.CombatPhase == MonsterCombatPhase.Returning),
            "a missing combat target also starts a visible smooth return");

        var boundaryRuntime = new MonsterMapRuntime(0, [definition], start);
        var radialTarget = new MonsterCombatTarget(
            CharacterId: target.CharacterId,
            X: definition.X + MonsterMapRuntime.CombatLeashRadius +
                MonsterMapRuntime.CombatRange - 0.1f,
            Z: definition.Z,
            IsAlive: true);
        boundaryRuntime.TryApplyDamage(
            definition.ObjectId,
            damage: 1,
            attackerCharacterId: radialTarget.CharacterId,
            now: start,
            out _);
        var boundaryNow = start;
        boundaryRuntime.Advance(boundaryNow, [radialTarget]);
        MonsterRuntimeSnapshot? radialArrival = null;
        for (var chaseTick = 0; chaseTick < 128 && radialArrival is null; chaseTick++)
        {
            boundaryNow += MonsterMapRuntime.TickInterval;
            boundaryRuntime.Advance(boundaryNow, [radialTarget]);
            var snapshot = boundaryRuntime.Snapshot().Single();
            if (snapshot.CombatPhase == MonsterCombatPhase.Attacking)
            {
                radialArrival = snapshot;
            }
        }

        Check.True(
            radialArrival is not null &&
            radialArrival.X - radialArrival.HomeX >
            MonsterMapRuntime.CombatLeashRadius - MonsterMapRuntime.CombatRange - 0.2f,
            "boundary fixture first reaches the outer attack ring without leashing");
        var tangentialTarget = new MonsterCombatTarget(
            CharacterId: target.CharacterId,
            X: radialArrival!.X,
            Z: radialArrival.HomeZ + 14f,
            IsAlive: true);
        MonsterRuntimeUpdate? predictedBoundaryReturn = null;
        for (var boundaryTick = 0; boundaryTick < 32 && predictedBoundaryReturn is null; boundaryTick++)
        {
            boundaryNow += MonsterMapRuntime.TickInterval;
            var tick = boundaryRuntime.Advance(boundaryNow, [tangentialTarget]);
            predictedBoundaryReturn = tick.Updates.SingleOrDefault(update =>
                update.Kind == MonsterRuntimeUpdateKind.Started &&
                update.Monster.CombatPhase == MonsterCombatPhase.Returning);
        }

        var boundaryTargetHomeDistance = Math.Sqrt(
            Math.Pow(tangentialTarget.X - definition.X, 2) +
            Math.Pow(tangentialTarget.Z - definition.Z, 2));
        Check.True(
            predictedBoundaryReturn is not null &&
            boundaryTargetHomeDistance <=
            MonsterMapRuntime.CombatLeashRadius + MonsterMapRuntime.CombatRange &&
            predictedBoundaryReturn.Monster.IsAlive &&
            predictedBoundaryReturn.Monster.IsSpawned,
            "predicted next chase step crossing the home boundary starts a visible return");
        return Task.CompletedTask;
    }
}
