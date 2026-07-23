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
    private static Task CheckSharedBoundedMonsterRuntimeAsync()
    {
        var initializedAt = new DateTimeOffset(2026, 5, 12, 17, 56, 0, TimeSpan.FromHours(12));
        var definition = CreateCapturedMonster(
            10001,
            176.979568f,
            -17.154812f,
            "A_normal_stub_001");
        var runtimeA = new MonsterMapRuntime(0, [definition], initializedAt);
        var runtimeB = new MonsterMapRuntime(0, [definition], initializedAt);
        var now = initializedAt;
        var starts = 0;
        var arrivals = 0;

        for (var tickIndex = 0; tickIndex < 8_000; tickIndex++)
        {
            now += MonsterMapRuntime.TickInterval;
            var tickA = runtimeA.Advance(now);
            var tickB = runtimeB.Advance(now);
            Check.Equal(tickA.PositionsChanged, tickB.PositionsChanged, "deterministic runtime movement flag");
            Check.Equal(tickA.Updates.Count, tickB.Updates.Count, "deterministic runtime update count");

            for (var updateIndex = 0; updateIndex < tickA.Updates.Count; updateIndex++)
            {
                var updateA = tickA.Updates[updateIndex];
                var updateB = tickB.Updates[updateIndex];
                Check.True(updateA.Kind == updateB.Kind, "deterministic runtime update kind");
                Check.Equal(updateA.Monster.X, updateB.Monster.X, "deterministic runtime update X");
                Check.Equal(updateA.Monster.Z, updateB.Monster.Z, "deterministic runtime update Z");

                if (updateA.Kind == MonsterRuntimeUpdateKind.Started)
                {
                    starts++;
                    var speed = MathF.Sqrt(
                        (updateA.Monster.VelocityX * updateA.Monster.VelocityX) +
                        (updateA.Monster.VelocityZ * updateA.Monster.VelocityZ));
                    Check.True(
                        MathF.Abs(speed - MonsterMapRuntime.MovementStep) < 0.00001f,
                        "roaming step magnitude is the captured 0.38 units");
                    Check.True(
                        updateA.Monster.MovementTicks is >= MonsterMapRuntime.MinimumMovementTicks and
                            <= MonsterMapRuntime.MaximumMovementTicks,
                        "roaming leg uses one to twenty-one captured ticks");
                }
                else if (updateA.Kind == MonsterRuntimeUpdateKind.Arrived)
                {
                    arrivals++;
                    var idleSeconds = (updateA.Monster.NextMovementAt - now).TotalSeconds;
                    Check.True(
                        idleSeconds >= 15 && idleSeconds <= 20.001,
                        "arrival schedules the next deterministic roam within 15-20 seconds");
                    var expectedFacing = MathF.Atan2(
                        updateA.Monster.VelocityX,
                        updateA.Monster.VelocityZ);
                    Check.True(
                        MathF.Abs(expectedFacing - updateA.Monster.Facing) < 0.00001f,
                        "arrival facing is atan2(dx,dz)");
                }
            }

            var snapshotA = runtimeA.Snapshot().Single();
            var snapshotB = runtimeB.Snapshot().Single();
            Check.Equal(snapshotA.X, snapshotB.X, "deterministic current X");
            Check.Equal(snapshotA.Z, snapshotB.Z, "deterministic current Z");
            var homeDistance = Math.Sqrt(
                Math.Pow(snapshotA.X - snapshotA.HomeX, 2) +
                Math.Pow(snapshotA.Z - snapshotA.HomeZ, 2));
            Check.True(
                homeDistance <= MonsterMapRuntime.MaximumRoamRadius + 0.0001,
                "every interpolated roaming position remains within eight units of home");
        }

        Check.True(starts >= 20 && arrivals >= 20, "bounded simulation exercises repeated roaming legs");
        Check.True(
            runtimeA.TryGetSnapshot(definition.ObjectId, out var current) && current.IsAlive && current.IsSpawned,
            "runtime exposes the current live monster snapshot");
        Check.True(
            !runtimeA.TryGetSnapshot(99999, out _),
            "runtime rejects an unknown monster snapshot");

        var map = new MapInstance(0);
        var sharedRuntime = map.InitializeMonsters([definition], initializedAt);
        var ignoredDefinition = CreateCapturedMonster(
            10002,
            definition.X + 10,
            definition.Z + 10,
            "A_normal_stub_003");
        var sameRuntime = map.InitializeMonsters([ignoredDefinition], initializedAt + TimeSpan.FromMinutes(1));
        Check.True(ReferenceEquals(sharedRuntime, sameRuntime), "map monster runtime initializes exactly once");
        Check.True(
            map.TryGetMonsterSnapshot(definition.ObjectId, out _) &&
            !map.TryGetMonsterSnapshot(ignoredDefinition.ObjectId, out _),
            "all viewers share the first authoritative map monster set");

        var lifecycle = new MonsterMapRuntime(0, [definition], initializedAt);
        var lethalAt = initializedAt + TimeSpan.FromSeconds(21);
        var movementStart = lifecycle.Advance(lethalAt);
        Check.True(
            movementStart.Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Started),
            "lifecycle fixture kills a monster during an active roaming leg");
        Check.True(
            lifecycle.TryApplyDamage(
                definition.ObjectId,
                1_000,
                lethalAt,
                out var damageResult),
            "atomic damage resolves a known monster");
        Check.Equal(237u, damageResult.BeforeHealth, "lethal damage before HP");
        Check.Equal(0u, damageResult.AfterHealth, "lethal damage after HP");
        var lethalPacket = PacketBuilder.SkillDamage(1, definition.ObjectId, 0, 1_000, 0, definition.X, definition.Z);
        Check.Equal(1_000u, ReadUInt32(lethalPacket, 16), "lethal packet keeps raw damage above remaining HP");
        Check.True(
            damageResult.Killed &&
            !damageResult.Monster.IsAlive &&
            damageResult.Monster.IsSpawned &&
            !damageResult.Monster.IsMoving,
            "death atomically stops roaming but retains the corpse");

        var deathTick = lifecycle.Advance(lethalAt);
        Check.True(
            deathTick.Updates.Select(update => update.Kind).SequenceEqual([MonsterRuntimeUpdateKind.Died]),
            "death emits exactly one immediate state event");
        lifecycle.Advance(lethalAt + TimeSpan.FromMilliseconds(4_999));
        Check.True(
            lifecycle.TryGetSnapshot(definition.ObjectId, out var corpse) && corpse.IsSpawned && !corpse.IsAlive,
            "corpse remains spawned until five seconds");
        var despawnTick = lifecycle.Advance(lethalAt + TimeSpan.FromSeconds(5));
        Check.True(
            despawnTick.Updates.Select(update => update.Kind).SequenceEqual([MonsterRuntimeUpdateKind.Despawned]),
            "corpse emits a despawn event at five seconds");
        Check.True(
            lifecycle.TryGetSnapshot(definition.ObjectId, out var despawned) && !despawned.IsSpawned,
            "despawned corpse leaves monster visibility");
        lifecycle.Advance(lethalAt + TimeSpan.FromMilliseconds(9_999));
        Check.True(
            lifecycle.TryGetSnapshot(definition.ObjectId, out var waiting) && !waiting.IsSpawned,
            "monster remains absent before the ten-second respawn");
        var respawnTick = lifecycle.Advance(lethalAt + TimeSpan.FromSeconds(10));
        Check.True(
            respawnTick.Updates.Select(update => update.Kind).SequenceEqual([MonsterRuntimeUpdateKind.Respawned]),
            "monster emits a respawn event at ten seconds");
        var respawned = respawnTick.Updates.Single().Monster;
        Check.True(respawned.IsAlive && respawned.IsSpawned, "respawn restores live spawned state");
        Check.Equal(respawned.MaximumHealth, respawned.CurrentHealth, "respawn restores full HP");
        Check.Equal(respawned.HomeX, respawned.X, "respawn returns to home X");
        Check.Equal(respawned.HomeZ, respawned.Z, "respawn returns to home Z");
        return Task.CompletedTask;
    }

    private static Task CheckMonsterRetaliationRuntimeAsync()
    {
        var start = new DateTimeOffset(2026, 5, 12, 17, 59, 50, TimeSpan.FromHours(12));
        var definition = CreateCapturedMonster(
            10013,
            100f,
            50f,
            "A_normal_stub_001",
            tier: 1,
            maximumHealth: 237);
        var target = new MonsterCombatTarget(
            CharacterId: 731,
            X: definition.X + 8.68f,
            Z: definition.Z,
            IsAlive: true);

        var passive = new MonsterMapRuntime(0, [definition], start);
        var passiveTick = passive.Advance(start + MonsterMapRuntime.TickInterval, [target]);
        Check.True(
            passiveTick.Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "nearby players do not proximity-aggro passive monsters");

        var runtime = new MonsterMapRuntime(0, [definition], start);
        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                now: start,
                out var hit) &&
            !hit.Killed,
            "a nonlethal hit attaches retaliation aggro");

        var chaseStart = runtime.Advance(start, [target]);
        var initialMovement = chaseStart.Updates.Single(update => update.Kind == MonsterRuntimeUpdateKind.Started);
        Check.Equal(0u, initialMovement.MovementMode, "initial combat chase uses movement mode zero");

        var now = start;
        var movementSteps = 0;
        MonsterRuntimeTick arrivalTick = new(false, []);
        while (movementSteps < 30)
        {
            now += MonsterMapRuntime.TickInterval;
            var tick = runtime.Advance(now, [target]);
            movementSteps++;
            if (tick.Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Arrived))
            {
                arrivalTick = tick;
                break;
            }

            var continuation = tick.Updates.Single(update => update.Kind == MonsterRuntimeUpdateKind.Started);
            Check.Equal(1u, continuation.MovementMode, "combat chase continuation uses movement mode one");
            if (movementSteps == 5)
            {
                Check.True(
                    runtime.TryApplyDamage(
                        definition.ObjectId,
                        damage: 1,
                        attackerCharacterId: target.CharacterId,
                        now,
                        out var repeatedChaseHit) &&
                    !repeatedChaseHit.Killed,
                    "a repeated hit from the aggro target preserves an active chase");
            }
        }

        Check.Equal(15, movementSteps, "8.68-unit chase reaches three-unit attack range in fifteen steps");
        var arrival = arrivalTick.Updates.Single(update => update.Kind == MonsterRuntimeUpdateKind.Arrived);
        Check.Equal(1u, arrival.MovementEndField ?? 0, "combat movement end carries field one");
        var distance = Math.Sqrt(
            Math.Pow(arrival.Monster.X - target.X, 2) +
            Math.Pow(arrival.Monster.Z - target.Z, 2));
        Check.True(distance <= MonsterMapRuntime.CombatRange + 0.0001, "combat chase stops within three units");

        now += MonsterMapRuntime.TickInterval;
        var firstAttack = runtime.Advance(now, [target]);
        var attack = firstAttack.Updates.Single(update => update.Kind == MonsterRuntimeUpdateKind.Attacked);
        Check.Equal(target.CharacterId, attack.TargetCharacterId ?? 0, "monster attacks the character who hit it");
        Check.Equal(target.X, attack.TargetX, "monster attack captures target X");
        Check.Equal(target.Z, attack.TargetZ, "monster attack captures target Z");

        for (var cooldownTick = 1; cooldownTick < MonsterMapRuntime.AttackCooldownTicks; cooldownTick++)
        {
            now += MonsterMapRuntime.TickInterval;
            if (cooldownTick == 5)
            {
                Check.True(
                    runtime.TryApplyDamage(
                        definition.ObjectId,
                        damage: 1,
                        attackerCharacterId: target.CharacterId,
                        now,
                        out var repeatedAttackHit) &&
                    !repeatedAttackHit.Killed,
                    "a repeated hit from the aggro target preserves the attack cooldown");
            }

            Check.True(
                runtime.Advance(now, [target]).Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
                $"monster does not attack early at cooldown tick {cooldownTick}");
        }

        now += MonsterMapRuntime.TickInterval;
        Check.True(
            runtime.Advance(now, [target]).Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Attacked),
            "monster repeats its attack exactly twenty-one ticks later");

        runtime.ClearAggroForCharacter(target.CharacterId, now);
        now += MonsterMapRuntime.AttackCooldown;
        Check.True(
            runtime.Advance(now, [target]).Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "clearing a disconnected/dead target stops retaliation");

        var lethal = new MonsterMapRuntime(0, [definition], start);
        Check.True(
            lethal.TryApplyDamage(
                definition.ObjectId,
                damage: 1_000,
                attackerCharacterId: target.CharacterId,
                now: start,
                out var lethalHit) && lethalHit.Killed,
            "lethal player damage resolves without retaliation");
        Check.True(
            lethal.Advance(start, [target]).Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "dead monsters never attack their killer");
        return Task.CompletedTask;
    }
}
