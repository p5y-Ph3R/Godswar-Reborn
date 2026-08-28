using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private const uint UnboundMedusaMonsterObjectId = 90_200;

    private static async Task
        CheckUnboundStatusHandlerCompatibilityAsync()
    {
        await CheckUnboundStatusHandlerCompatibilityAsync(
            PlayerRuntimeMode.Ecs);
        await CheckUnboundStatusHandlerCompatibilityAsync(
            PlayerRuntimeMode.Legacy);
    }

    private static async Task
        CheckUnboundStatusHandlerCompatibilityAsync(
            PlayerRuntimeMode playerRuntimeMode)
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var character = CreateRegistryDamageCharacter(
            700 + (int)playerRuntimeMode,
            mapId: 200);
        InstallMedusaHandlerEquipment(character);
        var store = new MedusaHandlerStore(character);
        var talents = new CountingTalentUpgradeExecutor();
        await using var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            playerRuntimeMode,
            gameplayCatalogs: GameplayContentTestFixtures.Runtime,
            itemContent: TestItemContent.Content);
        var created = await registry.CreateLocalWorldInstanceAsync(
            RealmId.Tempest,
            new MapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            CancellationToken.None);
        var runtime = created.Runtime ??
            throw new InvalidOperationException(
                "Explicit unbound handler runtime was not created.");
        registry.InitializeWorldInstanceMonsters(
            runtime.InstanceId,
            [CreateUnboundMedusaMonster()],
            DateTimeOffset.UtcNow);
        _ = GameHandlerOwnershipTestFences.Bind(
            registry,
            socket.Session,
            character.AccountId,
            character);
        registry.JoinWorldInstance(
            socket.Session,
            character.AccountId,
            character,
            MedusaHandlerLocalObjectId,
            runtime.InstanceId,
            worldReady: true,
            joinedAt: DateTimeOffset.UtcNow);
        var handler = CreateMedusaHandler(
            socket.Session,
            registry,
            character,
            store,
            talents);
        await PrepareMedusaMonsterVisibilityAsync(
            registry,
            socket.Session,
            character);

        try
        {
            var startX = character.PositionX;
            var startZ = character.PositionZ;
            await InvokeMedusaPacketAsync(
                handler,
                MedusaControlPacket(Opcodes.WalkBegin));
            await InvokeMedusaPacketAsync(
                handler,
                MedusaWalkPacket(startX + 0.25f, startZ + 0.25f));
            await InvokeMedusaPacketAsync(
                handler,
                MedusaControlPacket(Opcodes.WalkEnd));
            Check.True(
                character.PositionX == startX + 0.25f &&
                character.PositionZ == startZ + 0.25f &&
                character.PositionRevision > 0 &&
                store.PositionWrites > 0,
                $"ordinary unbound map 200 preserves WalkBegin/Walk/WalkEnd behavior for {playerRuntimeMode}");

            var target = RequiredMonster(
                runtime.Map,
                UnboundMedusaMonsterObjectId);
            await InvokeMedusaPacketAsync(
                handler,
                MedusaBasicAttackPacket(
                    character,
                    UnboundMedusaMonsterObjectId));
            Check.True(
                MedusaBasicCooldown(handler) >
                    DateTimeOffset.MinValue,
                $"ordinary unbound map 200 reaches the common basic-attack {playerRuntimeMode} backend");

            await InvokeMedusaPacketAsync(
                handler,
                MedusaSkillPacket(character, target));
            Check.True(
                store.SkillReads == 1 &&
                MedusaHasPendingCast(handler),
                $"ordinary unbound map 200 starts a pending skill for {playerRuntimeMode}");

            await InvokeMedusaPacketAsync(
                handler,
                MedusaTalentPacket());
            await InvokeMedusaPacketAsync(
                handler,
                MedusaEquipmentPacket());
            Check.True(
                talents.Executions == 1 &&
                store.EquipmentActivations == 1,
                $"ordinary unbound map 200 preserves opcodes 10049 and 10051 for {playerRuntimeMode}");
        }
        finally
        {
            await StopMedusaPendingCastsAsync(handler);
            registry.Remove(socket.Session);
        }
    }

    private static CapturedMonsterSpawn CreateUnboundMedusaMonster()
    {
        const string templateKey = "A_normal_stub_001";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 108);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0x0000_0212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            UnboundMedusaMonsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            10_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24),
            10_000_000);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40), 1f);
        Encoding.ASCII.GetBytes(templateKey).CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            MapId: 200,
            SceneKey: "Medusa_Island",
            templateKey,
            templateKey,
            UnboundMedusaMonsterObjectId,
            X: 1f,
            Z: 1f,
            packet);
    }
}
