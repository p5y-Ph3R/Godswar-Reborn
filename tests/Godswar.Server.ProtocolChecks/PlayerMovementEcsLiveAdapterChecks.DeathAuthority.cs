using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerMovementEcsLiveAdapterChecks
{
    private static async Task CheckDeadLegacyTcpMovementParityAsync()
    {
        foreach (var mode in new[]
                 {
                     PlayerRuntimeMode.Legacy,
                     PlayerRuntimeMode.Ecs
                 })
        {
            await CheckDeadLegacyTcpMovementRejectedAsync(mode);
        }
    }

    private static async Task CheckDeadLegacyTcpMovementRejectedAsync(
        PlayerRuntimeMode mode)
    {
        await using var actorSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var viewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var store = new RecordingPositionStore();
        var character = CreateCharacter(
            CharacterId + 20 + (int)mode,
            AccountId + 20 + (int)mode,
            $"DeadMovement{mode}");
        var viewer = CreateCharacter(
            ViewerCharacterId + 20 + (int)mode,
            ViewerAccountId + 20 + (int)mode,
            $"DeadMovementViewer{mode}");
        var registry = CreateRegistry(mode);
        registry.JoinMap(
            actorSocket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        registry.JoinMap(
            viewerSocket.Session,
            viewer.AccountId,
            viewer,
            WorldObjectIds.ForPlayer(viewer.Id));
        var handler = CreateHandler(
            actorSocket.Session,
            store,
            registry,
            character);

        character.CurrentHp = 0;
        SetField(handler, "_positionDirty", true);
        var initialX = character.PositionX;
        var initialZ = character.PositionZ;
        var initialRevision = character.PositionRevision;
        var packets = new[]
        {
            CreateMovementControlPacket(Opcodes.WalkBegin),
            CreateWalkPacket(
                opaqueMovementState: 0xABCD_1234u,
                targetX: 12f,
                targetZ: -34f),
            CreateMovementControlPacket(Opcodes.WalkEnd)
        };

        foreach (var packet in packets)
        {
            await InvokePacketAsync(handler, packet);
        }

        Check.Equal(
            initialX,
            character.PositionX,
            $"dead {mode} TCP movement preserves X");
        Check.Equal(
            initialZ,
            character.PositionZ,
            $"dead {mode} TCP movement preserves Z");
        Check.Equal(
            initialRevision,
            character.PositionRevision,
            $"dead {mode} TCP movement preserves position revision");
        Check.Equal(
            0,
            store.SaveAttempts,
            $"dead {mode} TCP movement never persists");
        Check.Equal(
            0,
            actorSocket.Available,
            $"dead {mode} TCP movement emits no self packet");
        Check.Equal(
            0,
            viewerSocket.Available,
            $"dead {mode} TCP movement emits no map broadcast");
        Check.True(
            handler.GetPlayerMovementEcsDiagnostics() is null,
            $"dead {mode} TCP movement never reaches the ECS adapter");
        Check.True(
            registry.TryGetMapSessionByCharacterId(
                character.CurrentMap,
                character.Id,
                excludeSession: null,
                out var context) &&
            ReferenceEquals(context.Session, actorSocket.Session) &&
            context.Character.CurrentHp == 0 &&
            context.Character.PositionX == initialX &&
            context.Character.PositionZ == initialZ,
            $"dead {mode} TCP movement preserves world membership");

        registry.Remove(actorSocket.Session);
        registry.Remove(viewerSocket.Session);
    }

    private static GamePacket CreateMovementControlPacket(
        ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        return new GamePacket(packet);
    }
}
