using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Monsters;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterEcsParityChecks
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        CheckTypedHydrationAndPatrolParity();
        CheckAggressiveProximityAndDamageThreatParity();
        CheckRangedReachAndFirstHitClaimParity();
        CheckDamageAggroAndStunParity();
        CheckDeathDespawnAndRespawnParity();
        CheckNeverRespawnParity();
        CheckLeashReturnAndReplacementParity();
        return Task.CompletedTask;
    }

    private static void CheckTypedHydrationAndPatrolParity()
    {
        var high = CreateMonster(10014, 104f, 50f);
        var low = CreateMonster(10013, 100f, 50f);
        var legacy = new MonsterMapRuntime(0, [high, low], Start);
        var ecs = new EcsMonsterMapRuntime(0, [high, low], Start);

        Check.Equal(2, ecs.World.EntityCount, "ECS monster entity count");
        Check.Equal(
            7,
            ecs.World.RegisteredComponentCount,
            "ECS monster component pool count");
        Check.True(
            ecs.World.Query<MonsterIdentityComponent>().Count() == 2,
            "captured monsters hydrate to typed ECS identities");
        AssertSnapshotsEqual(legacy, ecs, "initial hydration");

        var now = Start;
        for (var tickIndex = 0; tickIndex < 280; tickIndex++)
        {
            now += MonsterMapRuntime.TickInterval;
            AssertTickEqual(
                legacy.Advance(now),
                ecs.Advance(now),
                $"patrol tick {tickIndex}");
            AssertSnapshotsEqual(
                legacy,
                ecs,
                $"patrol snapshot {tickIndex}");
        }
    }

    private static void CheckDamageAggroAndStunParity()
    {
        var definition = CreateMonster(10013, 100f, 50f);
        var legacy = new MonsterMapRuntime(0, [definition], Start);
        var ecs = new EcsMonsterMapRuntime(0, [definition], Start);
        var target = new MonsterCombatTarget(
            CharacterId: 731,
            X: definition.X + 12f,
            Z: definition.Z,
            IsAlive: true);

        var legacyApplied = legacy.TryApplyDamage(
            definition.ObjectId,
            damage: 7,
            attackerCharacterId: target.CharacterId,
            now: Start,
            out var legacyDamage);
        var ecsApplied = ecs.TryApplyDamage(
            definition.ObjectId,
            damage: 7,
            attackerCharacterId: target.CharacterId,
            now: Start,
            out var ecsDamage);
        Check.Equal(legacyApplied, ecsApplied, "ECS damage accepted");
        Check.True(
            legacyDamage == ecsDamage,
            "ECS damage result matches legacy");
        AssertTickEqual(
            legacy.Advance(Start, [target]),
            ecs.Advance(Start, [target]),
            "aggro chase start");

        var now = Start;
        for (var tickIndex = 0; tickIndex < 4; tickIndex++)
        {
            now += MonsterMapRuntime.TickInterval;
            AssertTickEqual(
                legacy.Advance(now, [target]),
                ecs.Advance(now, [target]),
                $"pre-stun chase tick {tickIndex}");
        }

        var duration = TimeSpan.FromSeconds(3);
        var legacyStunned = legacy.TryApplyStun(
            definition.ObjectId,
            target.CharacterId,
            duration,
            now,
            out var legacyStun);
        var ecsStunned = ecs.TryApplyStun(
            definition.ObjectId,
            target.CharacterId,
            duration,
            now,
            out var ecsStun);
        Check.Equal(legacyStunned, ecsStunned, "ECS stun accepted");
        Check.True(legacyStun == ecsStun, "ECS stun result matches legacy");

        var stunEndsAt = now + duration;
        while (now <= stunEndsAt + MonsterMapRuntime.TickInterval)
        {
            AssertTickEqual(
                legacy.Advance(now, [target]),
                ecs.Advance(now, [target]),
                $"stun frame {now:O}");
            AssertSnapshotsEqual(
                legacy,
                ecs,
                $"stun snapshot {now:O}");
            now += MonsterMapRuntime.TickInterval;
        }

        legacy.ClearAggroForCharacter(target.CharacterId, now);
        ecs.ClearAggroForCharacter(target.CharacterId, now);
        AssertTickEqual(
            legacy.Advance(now, [target]),
            ecs.Advance(now, [target]),
            "clear-aggro return start");
        AssertSnapshotsEqual(legacy, ecs, "clear-aggro snapshot");
    }

    private static void CheckDeathDespawnAndRespawnParity()
    {
        var definition = CreateMonster(10013, 100f, 50f);
        var legacy = new MonsterMapRuntime(0, [definition], Start);
        var ecs = new EcsMonsterMapRuntime(0, [definition], Start);

        Check.True(
            legacy.TryApplyDamage(
                definition.ObjectId,
                uint.MaxValue,
                attackerCharacterId: 731,
                now: Start,
                out var legacyDeath),
            "legacy lethal damage accepted");
        Check.True(
            ecs.TryApplyDamage(
                definition.ObjectId,
                uint.MaxValue,
                attackerCharacterId: 731,
                now: Start,
                out var ecsDeath),
            "ECS lethal damage accepted");
        Check.True(legacyDeath == ecsDeath, "ECS death result matches legacy");

        AssertTickEqual(
            legacy.Advance(Start),
            ecs.Advance(Start),
            "death publication");
        AssertTickEqual(
            legacy.Advance(
                Start + MonsterMapRuntime.DefaultCorpseDespawnDelay),
            ecs.Advance(
                Start + MonsterMapRuntime.DefaultCorpseDespawnDelay),
            "corpse despawn");
        AssertTickEqual(
            legacy.Advance(Start + MonsterMapRuntime.DefaultRespawnDelay),
            ecs.Advance(Start + MonsterMapRuntime.DefaultRespawnDelay),
            "full-health respawn");
        AssertSnapshotsEqual(legacy, ecs, "post-respawn snapshot");

        var legacyStale = legacy.TryApplyDamage(
            definition.ObjectId,
            damage: 1,
            attackerCharacterId: 731,
            expectedSpawnGeneration: 1,
            now: Start + MonsterMapRuntime.DefaultRespawnDelay,
            out _);
        var ecsStale = ecs.TryApplyDamage(
            definition.ObjectId,
            damage: 1,
            attackerCharacterId: 731,
            expectedSpawnGeneration: 1,
            now: Start + MonsterMapRuntime.DefaultRespawnDelay,
            out _);
        Check.Equal(
            legacyStale,
            ecsStale,
            "stale spawn generation rejection parity");
    }

    private static void CheckLeashReturnAndReplacementParity()
    {
        var definition = CreateMonster(10013, 100f, 50f);
        var legacy = new MonsterMapRuntime(0, [definition], Start);
        var ecs = new EcsMonsterMapRuntime(0, [definition], Start);
        var target = new MonsterCombatTarget(
            CharacterId: 731,
            X: definition.X + 30f,
            Z: definition.Z,
            IsAlive: true);

        legacy.TryApplyDamage(
            definition.ObjectId,
            damage: 10,
            attackerCharacterId: target.CharacterId,
            now: Start,
            out _);
        ecs.TryApplyDamage(
            definition.ObjectId,
            damage: 10,
            attackerCharacterId: target.CharacterId,
            now: Start,
            out _);
        AssertTickEqual(
            legacy.Advance(Start, [target]),
            ecs.Advance(Start, [target]),
            "leash chase start");

        var now = Start;
        while (legacy.Snapshot().Single().X - definition.X <=
               MonsterMapRuntime.MaximumRoamRadius + 1f)
        {
            now += MonsterMapRuntime.TickInterval;
            AssertTickEqual(
                legacy.Advance(now, [target]),
                ecs.Advance(now, [target]),
                "leash chase");
        }

        target = target with
        {
            X = definition.X +
                MonsterMapRuntime.CombatLeashRadius +
                MonsterMapRuntime.CombatRange + 1f
        };
        now += MonsterMapRuntime.TickInterval;
        AssertTickEqual(
            legacy.Advance(now, [target]),
            ecs.Advance(now, [target]),
            "leash return start");

        var sawReturn = false;
        var sawRetirement = false;
        for (var tickIndex = 0; tickIndex < 200; tickIndex++)
        {
            now += MonsterMapRuntime.TickInterval;
            var legacyTick = legacy.Advance(now, [target]);
            var ecsTick = ecs.Advance(now, [target]);
            AssertTickEqual(
                legacyTick,
                ecsTick,
                $"leash return tick {tickIndex}");
            sawReturn |= legacyTick.Updates.Any(
                update => update.Kind == MonsterRuntimeUpdateKind.Returned);
            sawRetirement |= legacyTick.Updates.Any(
                update => update.Kind == MonsterRuntimeUpdateKind.Despawned);
            if (sawRetirement)
            {
                break;
            }
        }

        Check.True(sawReturn, "ECS parity trace reaches exact-home return");
        Check.True(sawRetirement, "ECS parity trace retires damaged generation");
        now += MonsterMapRuntime.TickInterval;
        AssertTickEqual(
            legacy.Advance(now, [target]),
            ecs.Advance(now, [target]),
            "leash replacement spawn");
        AssertSnapshotsEqual(legacy, ecs, "leash replacement snapshot");
    }

    private static void AssertTickEqual(
        MonsterRuntimeTick expected,
        MonsterRuntimeTick actual,
        string description)
    {
        Check.Equal(
            expected.PositionsChanged,
            actual.PositionsChanged,
            $"{description} position flag");
        Check.True(
            expected.Updates.SequenceEqual(actual.Updates),
            $"{description} ordered updates");
    }

    private static void AssertSnapshotsEqual(
        MonsterMapRuntime expected,
        EcsMonsterMapRuntime actual,
        string description) =>
        Check.True(
            expected.Snapshot().SequenceEqual(actual.Snapshot()),
            $"{description} snapshots");

    private static CapturedMonsterSpawn CreateMonster(
        uint objectId,
        float x,
        float z,
        uint tier = 1,
        byte mapId = 0,
        string templateKey = "A_normal_stub_001",
        string? displayName = null)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            tier);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            237);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            237);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32, 4),
            2f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey).CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            mapId,
            "Sparta",
            templateKey,
            displayName ?? templateKey,
            objectId,
            x,
            z,
            packet);
    }
}
