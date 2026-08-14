using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private const uint InterruptionMonsterObjectId = 19_026;

    private static async Task CheckBasicAttackCastInterruptionAsync()
    {
        await CheckLegacyBasicAttackCastInterruptionAsync();
        await CheckEcsBasicAttackCastInterruptionAsync();
        await CheckShockBasicAttackAdmissionAsync(
            PlayerRuntimeMode.Legacy);
        await CheckShockBasicAttackAdmissionAsync(
            PlayerRuntimeMode.Ecs);
    }

    private static async Task CheckLegacyBasicAttackCastInterruptionAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "BasicAttackInterruptedBackhaul",
            PlayerRuntimeMode.Legacy);
        fixture.Registry.InitializeMapMonsters(
            fixture.Character.CurrentMap,
            [CreateInterruptionMonster(fixture.Character)],
            TestTime);
        await using (var visibility =
            await fixture.Registry.BeginMonsterVisibilityTransitionAsync(
                fixture.Socket.Session,
                fixture.Character.CurrentMap,
                fixture.Character.PositionX,
                fixture.Character.PositionZ,
                CancellationToken.None) ??
            throw new InvalidOperationException(
                "Basic-attack interruption visibility was unavailable."))
        {
            visibility.Commit();
        }

        Check.True(
            fixture.Registry.TryGetMonsterSnapshot(
                fixture.Character.CurrentMap,
                InterruptionMonsterObjectId,
                out var target) &&
            fixture.Registry.IsMonsterVisibleTo(
                fixture.Socket.Session,
                target.ObjectId,
                target.SpawnGeneration),
            "basic-attack interruption fixture admits its target");
        await fixture.BeginCastAsync();
        await AssertCastStartedAsync(
            fixture,
            "basic-attack admission");

        await InvokePacketAsync(
            fixture.Handler,
            CreateControlPacket(Opcodes.BasicAttack));
        await AssertBasicAttackDidNotInterruptAsync(
            fixture,
            "malformed basic attack");

        await InvokePacketAsync(
            fixture.Handler,
            CreateInterruptionAttackPacket(
                fixture.Character,
                attackerObjectId: LocalPlayerObjectId + 1));
        await AssertBasicAttackDidNotInterruptAsync(
            fixture,
            "spoofed basic attack");

        fixture.Character.CurrentHp = 0;
        await InvokePacketAsync(
            fixture.Handler,
            CreateInterruptionAttackPacket(fixture.Character));
        fixture.Character.CurrentHp = 2_000;
        await AssertBasicAttackDidNotInterruptAsync(
            fixture,
            "dead-source basic attack");

        SetField(
            fixture.Handler,
            "_nextBasicAttackAt",
            DateTimeOffset.UtcNow.AddMinutes(1));
        await InvokePacketAsync(
            fixture.Handler,
            CreateInterruptionAttackPacket(fixture.Character));
        await AssertBasicAttackDidNotInterruptAsync(
            fixture,
            "cooldown-rejected basic attack");
        Check.True(
            !fixture.Registry.TryGetLatestAdmittedCombatRevision(
                fixture.Character.AccountId,
                fixture.Character.Id,
                out _),
            "rejected basic attacks consume no combat revision");

        SetField(
            fixture.Handler,
            "_nextBasicAttackAt",
            DateTimeOffset.MinValue);
        await InvokePacketAsync(
            fixture.Handler,
            CreateInterruptionAttackPacket(fixture.Character));
        await AssertInterruptedAsync(
            fixture,
            "admitted basic-attack interruption");
        Check.True(
            fixture.Registry.TryGetLatestAdmittedCombatRevision(
                fixture.Character.AccountId,
                fixture.Character.Id,
                out var revision) &&
            revision == 1,
            "the admitted attack interrupts and consumes one revision");
    }

    private static async Task CheckEcsBasicAttackCastInterruptionAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "EcsBasicAttackInterruptedBackhaul",
            PlayerRuntimeMode.Ecs);
        fixture.Registry.InitializeMapMonsters(
            fixture.Character.CurrentMap,
            [CreateInterruptionMonster(fixture.Character)],
            TestTime);
        await using (var visibility =
            await fixture.Registry.BeginMonsterVisibilityTransitionAsync(
                fixture.Socket.Session,
                fixture.Character.CurrentMap,
                fixture.Character.PositionX,
                fixture.Character.PositionZ,
                CancellationToken.None) ??
            throw new InvalidOperationException(
                "ECS attack interruption visibility was unavailable."))
        {
            visibility.Commit();
        }

        await fixture.BeginCastAsync();
        await AssertCastStartedAsync(
            fixture,
            "ECS basic-attack admission");
        SetField(
            fixture.Handler,
            "_nextBasicAttackAt",
            DateTimeOffset.UtcNow.AddMinutes(1));
        await InvokePacketAsync(
            fixture.Handler,
            CreateInterruptionAttackPacket(fixture.Character));
        await AssertBasicAttackDidNotInterruptAsync(
            fixture,
            "ECS cooldown-rejected basic attack");
        Check.True(
            !fixture.Registry.TryGetLatestAdmittedCombatRevision(
                fixture.Character.AccountId,
                fixture.Character.Id,
                out _),
            "ECS cooldown rejection occurs before its admission callback");

        SetField(
            fixture.Handler,
            "_nextBasicAttackAt",
            DateTimeOffset.MinValue);
        await InvokePacketAsync(
            fixture.Handler,
            CreateInterruptionAttackPacket(fixture.Character));
        await AssertInterruptedAsync(
            fixture,
            "admitted ECS basic-attack interruption");
        Check.True(
            fixture.Registry.TryGetLatestAdmittedCombatRevision(
                fixture.Character.AccountId,
                fixture.Character.Id,
                out var revision) &&
            revision == 1,
            "ECS admission claims the cast before applying its mutation");
    }

    private static async Task AssertBasicAttackDidNotInterruptAsync(
        InterruptFixture fixture,
        string description)
    {
        await Task.Delay(20);
        Check.True(
            HasPendingSkillCast(fixture.Handler),
            $"{description} preserves the pending cast");
        Check.Equal(
            0,
            fixture.Socket.Available,
            $"{description} publishes no interruption or combat packet");
    }

    private static GamePacket CreateInterruptionAttackPacket(
        GameCharacter character,
        uint attackerObjectId = LocalPlayerObjectId)
    {
        var packet = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.BasicAttack);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            attackerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            character.PositionZ);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            InterruptionMonsterObjectId);
        return new GamePacket(packet);
    }

    private static CapturedMonsterSpawn CreateInterruptionMonster(
        GameCharacter character)
    {
        const string templateKey = "A_normal_stub_001";
        var x = character.PositionX + 1f;
        var z = character.PositionZ;
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 108);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            InterruptionMonsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            10_000);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24),
            10_000);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40), 1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            character.CurrentMap,
            "Peloponnese",
            templateKey,
            templateKey,
            InterruptionMonsterObjectId,
            x,
            z,
            packet);
    }
}
