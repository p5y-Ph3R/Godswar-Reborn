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
    private static Task CheckNpcMovementCellVisibilityAsync()
    {
        var nearEast = CreateNpcDefinition(6001, 124.5f, -149f);
        var nextSouthRow = CreateNpcDefinition(6002, 64f, -165f);
        var oldNorthRow = CreateNpcDefinition(6003, 85f, -116f);
        var farAway = CreateNpcDefinition(6004, 10f, 10f);
        var tracker = new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
            [nearEast, nextSouthRow, oldNorthRow, farAway],
            npc => npc.ObjectId,
            npc => npc.X,
            npc => npc.Z,
            "NPC");

        Check.True(
            WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(-0.1f, -32f, out var negativeCell),
            "negative coordinates produce a valid NPC cell");
        Check.Equal(new WorldGridCell(-1, -1), negativeCell, "NPC cells use floor for negatives");
        Check.True(
            !WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(float.NaN, 0f, out _),
            "non-finite positions are rejected");
        Check.True(
            !WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(float.MaxValue, 0f, out _),
            "finite positions outside the grid range are rejected");

        Check.True(
            tracker.TryCalculate(85f, -119f, out var initial),
            "initial captured position resolves a visibility cell");
        Check.Equal(new WorldGridCell(2, -4), initial.PlayerCell, "initial captured player cell");
        Check.True(
            initial.Entering.Select(npc => npc.ObjectId).SequenceEqual([6001u, 6003u]),
            "initial 3x3 cell window contains only nearby NPCs");
        Check.Equal(0, initial.Leaving.Count, "initial visibility removes nothing");
        Check.True(!tracker.IsVisible(6001), "visibility is not committed before packets are sent");
        tracker.Commit(initial);
        Check.True(tracker.IsVisible(6001) && tracker.IsVisible(6003), "initial NPCs commit as visible");
        Check.True(!tracker.IsVisible(6002) && !tracker.IsVisible(6004), "outside NPCs remain hidden");

        Check.True(
            tracker.TryCalculate(92f, -127.9f, out var sameCell),
            "movement inside one cell is accepted");
        Check.Equal(0, sameCell.Entering.Count, "same-cell movement spawns nothing");
        Check.Equal(0, sameCell.Leaving.Count, "same-cell movement removes nothing");

        Check.True(
            tracker.TryCalculate(92f, -129f, out var southCrossing),
            "south cell crossing is accepted");
        Check.Equal(new WorldGridCell(2, -5), southCrossing.PlayerCell, "south crossing player cell");
        Check.True(
            southCrossing.Entering.Select(npc => npc.ObjectId).SequenceEqual([6002u]),
            "new southern NPC row enters after crossing z=-128");
        Check.Equal(0, southCrossing.Leaving.Count, "first south crossing keeps overlapping rows");
        Check.True(!tracker.IsVisible(6002), "new row waits for successful spawn send");
        tracker.Commit(southCrossing);

        Check.True(
            tracker.TryCalculate(92f, -161f, out var secondSouthCrossing),
            "second south cell crossing is accepted");
        Check.True(
            secondSouthCrossing.Leaving.SequenceEqual([6003u]),
            "old northern NPC row leaves after crossing z=-160");
        Check.Equal(0, secondSouthCrossing.Entering.Count, "second crossing has no synthetic entries");
        Check.True(tracker.IsVisible(6003), "old row waits for successful remove send");

        var removePacket = PacketBuilder.RemoveWorldObjects(secondSouthCrossing.Leaving.ToArray());
        Check.Equal((ushort)12, ReadUInt16(removePacket, 0), "single NPC remove packet length");
        Check.Equal((ushort)10024, ReadUInt16(removePacket, 2), "NPC remove opcode");
        Check.Equal(1u, ReadUInt32(removePacket, 4), "NPC remove count");
        Check.Equal(6003u, ReadUInt32(removePacket, 8), "NPC remove uses object ID");
        tracker.Commit(secondSouthCrossing);
        Check.True(!tracker.IsVisible(6003), "old row commits as hidden");

        return Task.CompletedTask;
    }

    private static Task CheckMonsterMovementCellVisibilityAsync()
    {
        // These positions and player transitions come from the working-server
        // monster capture and exercise both axes of the 32-unit sector grid.
        var eastMonster = CreateCapturedMonster(10004, 210.353653f, -17.122650f, "A_normal_stub_001");
        var westMonster = CreateCapturedMonster(10038, 143.051132f, -6.025902f, "A_normal_stub_001");
        var farWestMonster = CreateCapturedMonster(10042, 119.999641f, 13.100252f, "A_normal_stub_001");
        var northMonster = CreateCapturedMonster(10079, 141.978607f, 40.799419f, "A_normal_stub_003");
        var tracker = new WorldSectorVisibilityTracker<CapturedMonsterSpawn>(
            [westMonster, eastMonster, farWestMonster, northMonster],
            monster => monster.ObjectId,
            monster => monster.AppearanceX,
            monster => monster.AppearanceZ,
            "monster");

        Check.True(
            WorldObjectIds.IsReservedForPlayer(0x1448) &&
            WorldObjectIds.IsReservedForPlayer(0x6000) &&
            WorldObjectIds.IsReservedForPlayer(0x7FFF) &&
            !WorldObjectIds.IsReservedForPlayer(westMonster.ObjectId),
            "NPC and monster IDs cannot overlap the local or remote player namespace");

        westMonster.Validate(0);
        var roundedMetadata = westMonster with
        {
            X = westMonster.X + 0.00004f,
            Z = westMonster.Z - 0.00004f
        };
        roundedMetadata.Validate(0);
        Check.Equal(westMonster.X, roundedMetadata.AppearanceX, "packet X remains authoritative after metadata rounding");
        Check.Equal(westMonster.Z, roundedMetadata.AppearanceZ, "packet Z remains authoritative after metadata rounding");
        CreateCapturedMonster(10100, 1f, 1f, "field_monster", 0x00000112).Validate(0);
        CreateCapturedMonster(10101, 1f, 1f, "newbie_monster", 0x00040212).Validate(0);
        CreateCapturedMonster(10102, 1f, 1f, "elite_monster", 0x00040012).Validate(0);

        var capturedTierFourPacket = Convert.FromHexString(
            "6C00242712020000752700000400000000000000320100003201000017ED144300000000E0D55F42B70B05C0415F6E6F726D616C5F737475625F3030330000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        var capturedTierFour = new CapturedMonsterSpawn(
            0,
            "Sparta",
            "A_normal_stub_003",
            "captured tier-four monster",
            ReadUInt32(capturedTierFourPacket, 8),
            ReadSingle(capturedTierFourPacket, 28),
            ReadSingle(capturedTierFourPacket, 36),
            capturedTierFourPacket);
        capturedTierFour.Validate(0);
        Check.Equal(4u, ReadUInt32(capturedTierFourPacket, 12), "captured monster tier fixture");
        Check.Equal(306u, ReadUInt32(capturedTierFourPacket, 20), "captured monster HP metadata fixture");
        Check.True(
            PacketBuilder.CapturedMonsterSpawns([capturedTierFour]).SequenceEqual(capturedTierFourPacket),
            "captured tier-four appearance is replayed byte-for-byte");

        Check.True(
            tracker.TryCalculate(160.627f, -64.357f, out var initial),
            "initial monster position resolves a visibility cell");
        Check.Equal(new WorldGridCell(5, -3), initial.PlayerCell, "initial captured monster player cell");
        Check.Equal(0, initial.Entering.Count, "initial captured sector contains none of the fixture monsters");
        Check.Equal(0, initial.Leaving.Count, "initial monster visibility removes nothing");
        tracker.Commit(initial);

        Check.True(
            tracker.TryCalculate(160.9f, -64.1f, out var sameCell),
            "same-cell monster movement is accepted");
        Check.Equal(0, sameCell.Entering.Count, "same-cell movement spawns no monsters");
        Check.Equal(0, sameCell.Leaving.Count, "same-cell movement removes no monsters");

        Check.True(
            tracker.TryCalculate(160.627f, -63.638f, out var firstNorthCrossing),
            "first captured north crossing updates monster visibility");
        Check.True(
            firstNorthCrossing.Entering.Select(monster => monster.ObjectId).SequenceEqual([10004u, 10038u]),
            "captured first north crossing enters the observed monster row");
        Check.True(!tracker.IsVisible(10004), "monster visibility waits for a successful spawn send");

        var firstVisibleStream = PacketBuilder.CapturedMonsterSpawns(firstNorthCrossing.Entering);
        Check.Equal(eastMonster.Packet.Length + westMonster.Packet.Length, firstVisibleStream.Length, "nearby monster stream length");
        Check.Equal(10004u, ReadUInt32(firstVisibleStream, 8), "first nearby monster object ID");
        Check.Equal(10038u, ReadUInt32(firstVisibleStream, eastMonster.Packet.Length + 8), "second nearby monster object ID");
        tracker.Commit(firstNorthCrossing);

        Check.True(
            tracker.TryCalculate(159.841f, -50.757f, out var westCrossing),
            "captured west crossing updates monster visibility");
        Check.True(
            westCrossing.Leaving.SequenceEqual([10004u]),
            "captured west crossing removes the old eastern monster");
        var removePacket = PacketBuilder.RemoveWorldObjects(westCrossing.Leaving.ToArray());
        Check.Equal((ushort)10024, ReadUInt16(removePacket, 2), "monster remove opcode");
        Check.Equal(10004u, ReadUInt32(removePacket, 8), "monster remove uses captured object ID");
        tracker.Commit(westCrossing);

        Check.True(
            tracker.TryCalculate(157.447f, -31.132f, out var secondNorthCrossing),
            "second captured north crossing updates monster visibility");
        Check.True(
            secondNorthCrossing.Entering.Select(monster => monster.ObjectId).SequenceEqual([10042u]),
            "second captured north crossing enters the far-west monster");
        tracker.Commit(secondNorthCrossing);

        Check.True(
            tracker.TryCalculate(160.338f, -17.239f, out var eastCrossing),
            "captured east crossing updates monster visibility");
        Check.True(
            eastCrossing.Entering.Select(monster => monster.ObjectId).SequenceEqual([10004u]) &&
            eastCrossing.Leaving.SequenceEqual([10042u]),
            "one captured crossing can remove the old column and enter the new column");
        tracker.Commit(eastCrossing);

        Check.True(
            tracker.TryCalculate(175.733f, 0.970f, out var thirdNorthCrossing),
            "third captured north crossing updates monster visibility");
        Check.True(
            thirdNorthCrossing.Entering.Select(monster => monster.ObjectId).SequenceEqual([10079u]),
            "third captured north crossing enters the northern monster");
        tracker.Commit(thirdNorthCrossing);

        Check.True(
            tracker.TryCalculate(187.140f, -0.560f, out var finalSouthCrossing),
            "captured south crossing updates monster visibility");
        Check.True(
            finalSouthCrossing.Leaving.SequenceEqual([10079u]),
            "captured south crossing removes the northern monster");

        var mismatchedPacket = westMonster.Packet.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(mismatchedPacket.AsSpan(8, 4), westMonster.ObjectId + 1);
        var mismatchedMonster = westMonster with { Packet = mismatchedPacket };
        Check.Throws<InvalidDataException>(
            () => mismatchedMonster.Validate(0),
            "captured monster metadata mismatch is rejected");
        Check.Throws<InvalidDataException>(
            () => (westMonster with { X = westMonster.X + 0.01f }).Validate(0),
            "captured monster coordinate drift outside importer tolerance is rejected");

        return Task.CompletedTask;
    }

    private static Task CheckMonsterMovementPacketLayoutsAsync()
    {
        var capturedStart = Convert.FromHexString(
            "28002027112700000000000001000000E5FA3043000000000E3D89C1CD05A63E0000000047E14ABE");
        var generatedStart = PacketBuilder.MonsterMovementStart(
            ReadUInt32(capturedStart, 4),
            ReadSingle(capturedStart, 16),
            ReadSingle(capturedStart, 20),
            ReadSingle(capturedStart, 24),
            ReadSingle(capturedStart, 28),
            ReadSingle(capturedStart, 32),
            ReadSingle(capturedStart, 36));
        Check.True(
            generatedStart.SequenceEqual(capturedStart),
            "opcode-10016 movement start matches the working-server fixture byte-for-byte");
        Check.Equal((ushort)40, ReadUInt16(generatedStart, 0), "monster movement-start length");
        Check.Equal((ushort)10016, ReadUInt16(generatedStart, 2), "monster movement-start opcode");
        Check.Equal(10001u, ReadUInt32(generatedStart, 4), "monster movement-start object ID");
        Check.Equal(0u, ReadUInt32(generatedStart, 8), "monster movement-start reserved field");
        Check.Equal(1u, ReadUInt32(generatedStart, 12), "monster idle-roaming movement mode");

        var capturedEnd = Convert.FromHexString(
            "220021271127000000000000060000002A0B32430000000063D398C1107C2F400000");
        var generatedEnd = PacketBuilder.MonsterMovementEnd(
            ReadUInt32(capturedEnd, 4),
            ReadUInt32(capturedEnd, 12),
            ReadSingle(capturedEnd, 16),
            ReadSingle(capturedEnd, 20),
            ReadSingle(capturedEnd, 24),
            ReadSingle(capturedEnd, 28));
        Check.True(
            generatedEnd.SequenceEqual(capturedEnd),
            "opcode-10017 movement end matches the working-server fixture byte-for-byte");
        Check.Equal((ushort)34, ReadUInt16(generatedEnd, 0), "monster movement-end length");
        Check.Equal((ushort)10017, ReadUInt16(generatedEnd, 2), "monster movement-end opcode");
        Check.Equal(6u, ReadUInt32(generatedEnd, 12), "monster movement-end tick count");
        Check.Equal((ushort)0, ReadUInt16(generatedEnd, 32), "monster movement-end trailing field");

        var capturedLifecycleMarker = Convert.FromHexString("0800272734270000");
        Check.True(
            PacketBuilder.MonsterLifecycleMarker(10036).SequenceEqual(capturedLifecycleMarker),
            "opcode-10023 corpse/respawn marker matches the working-server fixture byte-for-byte");
        return Task.CompletedTask;
    }

    private static async Task CheckPersistedWorldBossRespawnAsync()
    {
        var initializedAt = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        var respawnAt = initializedAt.AddHours(6);
        var definition = CreateCapturedMonster(
            12003,
            25f,
            -30f,
            "A_boss_boar_001",
            mapId: 3,
            sceneKey: "Parnitha_1");
        var persisted = new WorldBossRespawnState(3, definition.TemplateKey, respawnAt);
        var runtime = new MonsterMapRuntime(
            3,
            [definition],
            initializedAt,
            activeWorldBossRespawn: persisted);

        var suppressed = runtime.Snapshot().Single();
        Check.True(!suppressed.IsAlive, "persisted world boss remains dead after server restart");
        Check.True(!suppressed.IsSpawned, "persisted world boss remains hidden before its next cycle");
        Check.True(suppressed.RespawnAt == respawnAt, "persisted world-boss respawn timestamp is restored");

        var beforeRespawn = runtime.Advance(respawnAt.AddTicks(-1));
        Check.Equal(0, beforeRespawn.Updates.Count, "world boss does not respawn before persisted expiry");
        var atRespawn = runtime.Advance(respawnAt);
        Check.Equal(1, atRespawn.Updates.Count, "world boss respawns exactly at persisted expiry");
        Check.True(
            atRespawn.Updates[0].Kind == MonsterRuntimeUpdateKind.Respawned,
            "persisted lifecycle emits respawn event");
        Check.True(atRespawn.Updates[0].Monster.IsAlive, "respawned world boss is alive");
        await CheckJsonFocusedWorldBossPersistenceAsync();
    }

    private static Task CheckMonsterRuntimeAppearancePatchAsync()
    {
        var monster = CreateCapturedMonster(
            10038,
            143.051132f,
            -6.025902f,
            "A_normal_stub_001");
        monster.Packet[16] = 0xA5;
        monster.Packet[17] = 0x5A;
        monster.Packet[107] = 0xC3;
        BinaryPrimitives.WriteSingleLittleEndian(monster.Packet.AsSpan(32, 4), 7.25f);
        var original = monster.Packet.ToArray();
        var state = new CapturedMonsterAppearanceState(
            monster,
            150.25f,
            -12.5f,
            -2.25f,
            123,
            456);

        var patched = PacketBuilder.CapturedMonsterAppearance(state);
        Check.Equal(123u, ReadUInt32(patched, 20), "runtime appearance current HP");
        Check.Equal(456u, ReadUInt32(patched, 24), "runtime appearance maximum HP");
        Check.Equal(state.X, ReadSingle(patched, 28), "runtime appearance X");
        Check.Equal(7.25f, ReadSingle(patched, 32), "runtime appearance preserves captured Y");
        Check.Equal(state.Z, ReadSingle(patched, 36), "runtime appearance Z");
        Check.Equal(state.Facing, ReadSingle(patched, 40), "runtime appearance facing");
        Check.True(monster.Packet.SequenceEqual(original), "runtime appearance does not mutate the capture template");

        for (var offset = 0; offset < original.Length; offset++)
        {
            var patchedField = offset is >= 20 and < 32 or >= 36 and < 44;
            if (!patchedField)
            {
                Check.Equal(original[offset], patched[offset], $"runtime appearance preserves byte {offset}");
            }
        }

        var stream = PacketBuilder.CapturedMonsterSpawns([state, state]);
        Check.Equal(patched.Length * 2, stream.Length, "runtime appearance stream length");
        Check.True(
            stream.AsSpan(0, patched.Length).SequenceEqual(patched) &&
            stream.AsSpan(patched.Length, patched.Length).SequenceEqual(patched),
            "runtime appearance stream contains patched packets in order");
        return Task.CompletedTask;
    }
}
