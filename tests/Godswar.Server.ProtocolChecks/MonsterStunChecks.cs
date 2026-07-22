using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterStunChecks
{
    public static Task RunAsync()
    {
        CheckCatalog();
        CheckStatusPacket();
        CheckMovementAndAttackSuppression();
        return Task.CompletedTask;
    }

    private static void CheckCatalog()
    {
        var expected = new[]
        {
            (SkillId: 70, Cooldown: 30, Odds: 150, Priority: 1),
            (SkillId: 71, Cooldown: 26, Odds: 190, Priority: 2),
            (SkillId: 72, Cooldown: 23, Odds: 200, Priority: 3),
            (SkillId: 73, Cooldown: 20, Odds: 230, Priority: 4),
            (SkillId: 74, Cooldown: 18, Odds: 250, Priority: 5)
        };

        Check.True(
            expected.All(static item => item.Odds > 100),
            "stun StatusOdds values are ratings, not percentage chances");
        Check.Equal(expected.Length, MonsterStunSkillCatalog.Count, "warrior stun catalog count");
        foreach (var item in expected)
        {
            Check.True(
                MonsterStunSkillCatalog.TryGet(item.SkillId, out var definition),
                $"stun skill {item.SkillId} resolves");
            Check.Equal(
                MonsterStunSkillCatalog.StunnedStatusId,
                definition.StatusId,
                $"stun skill {item.SkillId} status ID");
            Check.Equal(
                MonsterStunSkillCatalog.StunDuration,
                definition.Duration,
                $"stun skill {item.SkillId} duration");
            Check.Equal(
                TimeSpan.FromSeconds(item.Cooldown),
                definition.Cooldown,
                $"stun skill {item.SkillId} cooldown");
            Check.Equal(item.Odds, definition.StatusOdds, $"stun skill {item.SkillId} status odds");
            Check.Equal(item.Priority, definition.Priority, $"stun skill {item.SkillId} priority");
        }

        Check.True(!MonsterStunSkillCatalog.TryGet(69, out _), "non-stun skill is absent");
    }

    private static void CheckStatusPacket()
    {
        var packet = PacketBuilder.WorldObjectStatusEffects(
            10013,
            [new ClientStatusEffect(MonsterStunSkillCatalog.StunnedStatusId, 3)]);

        Check.Equal((ushort)340, ReadUInt16(packet, 0), "monster status packet length");
        Check.Equal((ushort)10167, ReadUInt16(packet, 2), "monster status packet opcode");
        Check.Equal(10013u, ReadUInt32(packet, 4), "monster status target object ID");
        Check.Equal(1u, ReadUInt32(packet, 8), "monster status count");
        Check.Equal(331u, ReadUInt32(packet, 12), "monster stunned status ID");
        Check.Equal(3u, ReadUInt32(packet, 92), "monster stunned status timer");
        Check.Equal(0u, ReadUInt32(packet, 172), "monster status leaves player max-HP data unset");
        Check.Equal(1f, ReadSingle(packet, 324), "monster status movement multiplier baseline");
    }

    private static void CheckMovementAndAttackSuppression()
    {
        var start = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
        var definition = CreateCapturedMonster(10013, 100f, 50f);
        var target = new MonsterCombatTarget(
            CharacterId: 731,
            X: definition.X + 8f,
            Z: definition.Z,
            IsAlive: true);
        var runtime = new MonsterMapRuntime(0, [definition], start);

        Check.True(
            runtime.TryApplyDamage(
                definition.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                now: start,
                out var openingHit) &&
            !openingHit.Killed,
            "stun fixture establishes monster retaliation");
        Check.True(
            runtime.Advance(start, [target]).Updates.Any(update =>
                update.Kind == MonsterRuntimeUpdateKind.Started),
            "monster begins chasing before stun");

        var stunAt = start + MonsterMapRuntime.TickInterval;
        runtime.Advance(stunAt, [target]);
        var beforeStun = runtime.Snapshot().Single();
        Check.True(beforeStun.IsMoving, "monster is moving immediately before stun");
        Check.True(
            runtime.TryApplyStun(
                definition.ObjectId,
                target.CharacterId,
                MonsterStunSkillCatalog.StunDuration,
                stunAt,
                out var stun) &&
            stun.Applied,
            "known live monster accepts stun");
        Check.Equal(
            stunAt + MonsterStunSkillCatalog.StunDuration,
            stun.StunnedUntil!.Value,
            "stun expiry timestamp");
        Check.True(stun.Monster.IsStunned, "stun snapshot exposes active control state");
        Check.True(!stun.Monster.IsMoving, "stun atomically stops an active chase");
        Check.Equal(
            openingHit.AfterHealth,
            stun.Monster.CurrentHealth,
            "stun applies no damage");

        var stopTick = runtime.Advance(stunAt, [target]);
        var movementEnd = stopTick.Updates.Single(update =>
            update.Kind == MonsterRuntimeUpdateKind.Arrived);
        Check.Equal(1u, movementEnd.MovementEndField ?? 0, "stun emits authoritative movement end");
        var stoppedX = stun.Monster.X;
        var stoppedZ = stun.Monster.Z;
        var expiresAt = stun.StunnedUntil.Value;
        for (var now = stunAt + MonsterMapRuntime.TickInterval;
             now < expiresAt;
             now += MonsterMapRuntime.TickInterval)
        {
            var tick = runtime.Advance(now, [target]);
            Check.True(
                tick.Updates.All(update =>
                    update.Kind is not MonsterRuntimeUpdateKind.Started and
                    not MonsterRuntimeUpdateKind.Attacked),
                "stunned monster neither moves nor attacks before expiry");
            var frozen = runtime.Snapshot().Single();
            Check.Equal(stoppedX, frozen.X, "stunned monster X remains fixed");
            Check.Equal(stoppedZ, frozen.Z, "stunned monster Z remains fixed");
            Check.True(frozen.IsStunned, "stun remains active before expiry");
        }

        var resume = runtime.Advance(expiresAt, [target]);
        Check.True(
            resume.Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Started),
            "monster resumes chasing at stun expiry");
        Check.True(!runtime.Snapshot().Single().IsStunned, "stun state clears at expiry");

        var closeTarget = target with { X = definition.X + 2f };
        var attackRuntime = new MonsterMapRuntime(0, [definition], start);
        attackRuntime.TryApplyDamage(
            definition.ObjectId,
            damage: 1,
            attackerCharacterId: closeTarget.CharacterId,
            now: start,
            out _);
        attackRuntime.Advance(start, [closeTarget]);
        Check.True(
            attackRuntime.TryApplyStun(
                definition.ObjectId,
                closeTarget.CharacterId,
                MonsterStunSkillCatalog.StunDuration,
                start,
                out var closeStun) &&
            closeStun.Applied,
            "in-range attacking monster accepts stun");
        Check.True(
            attackRuntime.Advance(
                    start + MonsterMapRuntime.TickInterval,
                    [closeTarget])
                .Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "stun suppresses an imminent monster strike");
        var closeExpiry = closeStun.StunnedUntil!.Value;
        Check.True(
            attackRuntime.Advance(closeExpiry, [closeTarget])
                .Updates.All(update => update.Kind != MonsterRuntimeUpdateKind.Attacked),
            "monster does not replay a catch-up strike at expiry");
        Check.True(
            attackRuntime.Advance(closeExpiry + MonsterMapRuntime.TickInterval, [closeTarget])
                .Updates.Any(update => update.Kind == MonsterRuntimeUpdateKind.Attacked),
            "monster can attack on the first authoritative post-stun tick");

        var lethalRuntime = new MonsterMapRuntime(0, [definition], start);
        lethalRuntime.TryApplyStun(
            definition.ObjectId,
            target.CharacterId,
            MonsterStunSkillCatalog.StunDuration,
            start,
            out _);
        Check.True(
            lethalRuntime.TryApplyDamage(
                definition.ObjectId,
                damage: uint.MaxValue,
                attackerCharacterId: target.CharacterId,
                now: start,
                out var lethal) &&
            lethal.Killed &&
            !lethal.Monster.IsStunned,
            "monster death clears stun state");
        Check.True(
            !runtime.TryApplyStun(
                99999,
                target.CharacterId,
                MonsterStunSkillCatalog.StunDuration,
                start,
                out _),
            "unknown monster rejects stun");
    }

    private static CapturedMonsterSpawn CreateCapturedMonster(uint objectId, float x, float z)
    {
        const string templateKey = "A_normal_stub_001";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), 237);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24, 4), 237);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40, 4), 1f);
        Encoding.ASCII.GetBytes(templateKey).CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            0,
            "Sparta",
            templateKey,
            templateKey,
            objectId,
            x,
            z,
            packet);
    }

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset, sizeof(ushort)));

    private static uint ReadUInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, sizeof(uint)));

    private static float ReadSingle(byte[] packet, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset, sizeof(float)));
}
