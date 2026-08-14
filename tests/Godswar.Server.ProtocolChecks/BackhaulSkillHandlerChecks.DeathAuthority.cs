using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private static async Task CheckDeadMovementAndReviveAuthorityAsync()
    {
        foreach (var mode in new[]
                 {
                     PlayerRuntimeMode.Legacy,
                     PlayerRuntimeMode.Ecs
                 })
        {
            await CheckDeadMovementDoesNotInterruptCastAsync(mode);
        }

        await CheckReviveAdmissionRejectsWithoutMutationAsync();
        await CheckCaptureProvenFreeReviveAsync();
    }

    private static async Task CheckDeadMovementDoesNotInterruptCastAsync(
        PlayerRuntimeMode mode)
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            $"DeadMovementCast{mode}",
            mode);
        await fixture.BeginCastAsync();
        await AssertCastStartedAsync(
            fixture,
            $"dead {mode} movement gate");

        fixture.Character.CurrentHp = 0;
        SetField(fixture.Handler, "_positionDirty", true);
        var initialX = fixture.Character.PositionX;
        var initialZ = fixture.Character.PositionZ;
        var initialPositionRevision =
            fixture.Character.PositionRevision;
        var initialLifeRevision =
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);
        var movements = new[]
        {
            CreateControlPacket(Opcodes.WalkBegin),
            CreateAuthorityWalkPacket(
                fixture.Character.PositionX + 5f,
                fixture.Character.PositionZ - 5f),
            CreateControlPacket(Opcodes.WalkEnd)
        };

        foreach (var movement in movements)
        {
            await InvokePacketAsync(fixture.Handler, movement);
            Check.True(
                HasPendingSkillCast(fixture.Handler),
                $"dead {mode} opcode {movement.Opcode} preserves pending cast");
        }

        await Task.Delay(50);
        Check.Equal(
            0,
            fixture.Socket.Available,
            $"dead {mode} movement emits no interrupt or movement packet");
        Check.Equal(
            initialX,
            fixture.Character.PositionX,
            $"dead {mode} movement preserves X");
        Check.Equal(
            initialZ,
            fixture.Character.PositionZ,
            $"dead {mode} movement preserves Z");
        Check.Equal(
            initialPositionRevision,
            fixture.Character.PositionRevision,
            $"dead {mode} movement preserves position revision");
        Check.Equal(
            0,
            fixture.Store.PositionWrites.Count,
            $"dead {mode} movement never persists position");
        Check.Equal(
            0,
            fixture.Store.VitalsWrites.Count,
            $"dead {mode} movement never persists vitals");
        Check.Equal(
            initialLifeRevision,
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session),
            $"dead {mode} movement preserves life revision");
    }

    private static async Task CheckReviveAdmissionRejectsWithoutMutationAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "ReviveAdmissionAuthority");
        fixture.Character.CurrentHp = 0;
        fixture.Character.CurrentMp = 77;

        var valid = CreateRevivePacket(
            LocalPlayerObjectId,
            ReviveRequest.FreeReviveType);
        Check.True(
            ReviveRequest.TryParse(valid.Buffer, out var parsed) &&
            parsed.PlayerObjectId == LocalPlayerObjectId &&
            parsed.ReviveType == ReviveRequest.FreeReviveType,
            "capture-proven exact free-revive frame parses");

        var trailing = CreateRevivePacket(
            LocalPlayerObjectId,
            ReviveRequest.FreeReviveType,
            actualLength: 13,
            declaredLength: 12);
        var wrongOpcode = CreateRevivePacket(
            LocalPlayerObjectId,
            ReviveRequest.FreeReviveType,
            opcode: Opcodes.Ping);
        Check.True(
            !ReviveRequest.TryParse(trailing.Buffer, out _),
            "revive parser rejects trailing bytes beyond the exact frame");
        Check.True(
            !ReviveRequest.TryParse(wrongOpcode.Buffer, out _),
            "revive parser rejects another opcode with the same shape");

        var rejected = new[]
        {
            CreateMalformedRevivePacket(),
            trailing,
            CreateRevivePacket(
                LocalPlayerObjectId + 1,
                ReviveRequest.FreeReviveType),
            CreateRevivePacket(
                LocalPlayerObjectId,
                reviveType: 1)
        };
        foreach (var request in rejected)
        {
            await AssertReviveRejectedWithoutMutationAsync(
                fixture,
                request,
                $"rejected revive opcode={request.Opcode} length={request.Length}");
        }

        fixture.Character.CurrentHp = 25;
        await AssertReviveRejectedWithoutMutationAsync(
            fixture,
            valid,
            "living-character free revive");
    }

    private static async Task CheckCaptureProvenFreeReviveAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "CaptureProvenFreeRevive");
        var character = fixture.Character;
        character.CurrentHp = 0;
        character.CurrentMp = 0;
        SetField(fixture.Handler, "_characterSnapshotLoaded", true);
        SetField(
            fixture.Handler,
            "_characterSnapshotBootstrapPending",
            true);
        SetField(
            fixture.Handler,
            "_characterLoadSnapshot",
            new HydratedCharacterLoadSnapshot(
                character,
                [],
                [],
                new CharacterPetShedSnapshot(2, 0),
                [],
                []));
        var initialLifeRevision =
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);

        await InvokePacketAsync(
            fixture.Handler,
            CreateRevivePacket(
                LocalPlayerObjectId,
                ReviveRequest.FreeReviveType));

        Check.Equal(
            initialLifeRevision + 1,
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session),
            "free revive advances life revision exactly once");
        Check.Equal(
            GameDefaults.SpartaCapitalMap,
            character.CurrentMap,
            "free revive restores the camp capital");
        Check.Equal(
            GameDefaults.StartingPositionX,
            character.PositionX,
            "free revive restores starting X");
        Check.Equal(
            GameDefaults.StartingPositionZ,
            character.PositionZ,
            "free revive restores starting Z");
        Check.Equal(
            character.MaxHp / 10,
            character.CurrentHp,
            "free revive restores ten percent HP");
        Check.Equal(
            character.MaxMp / 10,
            character.CurrentMp,
            "free revive restores ten percent MP");
        Check.Equal(
            1,
            fixture.Store.PositionWrites.Count,
            "free revive persists one position checkpoint");
        Check.Equal(
            1,
            fixture.Store.VitalsWrites.Count,
            "free revive persists one vitals checkpoint");
        Check.True(
            fixture.Store.PositionWrites[0] is
            {
                MapId: GameDefaults.SpartaCapitalMap,
                X: GameDefaults.StartingPositionX,
                Z: GameDefaults.StartingPositionZ
            },
            "free revive persists the restored position");
        Check.True(
            fixture.Store.VitalsWrites[0] is
            {
                CurrentHp: 250,
                CurrentMp: 150
            },
            "free revive persists the restored vitals");

        var reachedEnterComplete = false;
        // An empty 24-slot bag still emits its complete detail/index pages.
        // Keep the read bounded above that deterministic bootstrap size.
        for (var index = 0; index < 128; index++)
        {
            var response = await fixture.Socket.ReadPacketAsync();
            if (ReadUInt16(response, 2) == Opcodes.GameServerReady)
            {
                reachedEnterComplete = true;
                break;
            }
        }
        Check.True(
            reachedEnterComplete,
            "free revive completes the bounded re-entry bootstrap");
    }

    private static async Task AssertReviveRejectedWithoutMutationAsync(
        InterruptFixture fixture,
        GamePacket request,
        string description)
    {
        var character = fixture.Character;
        var initialMap = character.CurrentMap;
        var initialX = character.PositionX;
        var initialZ = character.PositionZ;
        var initialHp = character.CurrentHp;
        var initialMp = character.CurrentMp;
        var initialPositionRevision = character.PositionRevision;
        var initialVitalsRevision = character.VitalsRevision;
        var initialLifeRevision =
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);

        await InvokePacketAsync(fixture.Handler, request);

        Check.Equal(initialMap, character.CurrentMap,
            $"{description} preserves map");
        Check.Equal(initialX, character.PositionX,
            $"{description} preserves X");
        Check.Equal(initialZ, character.PositionZ,
            $"{description} preserves Z");
        Check.Equal(initialHp, character.CurrentHp,
            $"{description} preserves HP");
        Check.Equal(initialMp, character.CurrentMp,
            $"{description} preserves MP");
        Check.Equal(initialPositionRevision, character.PositionRevision,
            $"{description} preserves position revision");
        Check.Equal(initialVitalsRevision, character.VitalsRevision,
            $"{description} preserves vitals revision");
        Check.Equal(
            initialLifeRevision,
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session),
            $"{description} preserves life revision");
        Check.Equal(
            0,
            fixture.Store.PositionWrites.Count,
            $"{description} persists no position");
        Check.Equal(
            0,
            fixture.Store.VitalsWrites.Count,
            $"{description} persists no vitals");
        Check.Equal(
            0,
            fixture.Socket.Available,
            $"{description} emits no lifecycle packet");
        Check.True(
            fixture.Registry.TryGetMapSessionByCharacterId(
                character.CurrentMap,
                character.Id,
                excludeSession: null,
                out var context) &&
            ReferenceEquals(
                context.Session,
                fixture.Socket.Session),
            $"{description} preserves registered world membership");
    }

    private static GamePacket CreateAuthorityWalkPacket(
        float targetX,
        float targetZ)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Walk);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0xCAFE_BABEu);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            targetX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12),
            targetZ);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            1f);
        return new GamePacket(packet);
    }

    private static GamePacket CreateMalformedRevivePacket()
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.Revive);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            LocalPlayerObjectId);
        return new GamePacket(packet);
    }

    private static GamePacket CreateRevivePacket(
        uint playerObjectId,
        int reviveType,
        int actualLength = 12,
        ushort declaredLength = 12,
        ushort opcode = Opcodes.Revive)
    {
        var packet = new byte[actualLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            declaredLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            playerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            reviveType);
        return new GamePacket(packet);
    }
}
