using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class Program
{
    private const int ConcurrentPacketCount = 512;
    private const int ConcurrentPacketLength = 37;
    private const ushort ConcurrentPacketOpcode = 0x6F6F;

    public static async Task<int> Main()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("Character camp starting location", CheckCharacterCampStartingLocationAsync),
            ("PlayerWorldSpawn layout", CheckPlayerWorldSpawnAsync),
            ("PlayerWorldSpawn captured appearance", CheckPlayerWorldAppearanceAsync),
            ("PlayerWorldSpawn full quality/grade extension", CheckPlayerWorldExtendedAppearanceAsync),
            ("Player auxiliary appearance packets", CheckPlayerAuxiliaryAppearanceAsync),
            ("PlayerInspectEquipment packed slots and details", CheckPlayerInspectExtendedSlotsAsync),
            ("PlayerStatusUpdate layout", CheckPlayerStatusUpdateAsync),
            ("NPC definitions and spawn layout", CheckNpcDefinitionsAndSpawnLayoutAsync),
            ("NPC movement-cell visibility", CheckNpcMovementCellVisibilityAsync),
            ("Monster movement-cell visibility and spawn layout", CheckMonsterMovementCellVisibilityAsync),
            ("Map registry world-readiness gate", CheckMapRegistryWorldReadinessAsync),
            ("ClientSession concurrent send ordering", CheckConcurrentSendOrderingAsync)
        };

        var failures = 0;
        foreach (var check in checks)
        {
            try
            {
                await check.Run();
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {check.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Protocol checks: {checks.Length - failures} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static Task CheckCharacterCampStartingLocationAsync()
    {
        var sparta = new GameCharacter
        {
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = GameDefaults.AthensCapitalMap,
            PositionX = 10f,
            PositionZ = 20f
        };
        GameDefaults.InitializeStartingLocation(sparta);

        Check.Equal(GameDefaults.SpartaCamp, sparta.Camp, "Sparta camp is preserved");
        Check.Equal(GameDefaults.SpartaCapitalMap, sparta.CurrentMap, "Sparta starts on map 0");
        Check.Equal(GameDefaults.StartingPositionX, sparta.PositionX, "Sparta starting X");
        Check.Equal(GameDefaults.StartingPositionZ, sparta.PositionZ, "Sparta starting Z");

        var athens = new GameCharacter
        {
            Camp = GameDefaults.AthensCamp,
            CurrentMap = GameDefaults.SpartaCapitalMap,
            PositionX = 10f,
            PositionZ = 20f
        };
        GameDefaults.InitializeStartingLocation(athens);

        Check.Equal(GameDefaults.AthensCamp, athens.Camp, "Athens camp is preserved");
        Check.Equal(GameDefaults.AthensCapitalMap, athens.CurrentMap, "Athens starts on map 1");
        Check.Equal(GameDefaults.StartingPositionX, athens.PositionX, "Athens starting X");
        Check.Equal(GameDefaults.StartingPositionZ, athens.PositionZ, "Athens starting Z");

        var invalid = new GameCharacter { Camp = byte.MaxValue };
        GameDefaults.InitializeStartingLocation(invalid);

        Check.Equal(GameDefaults.AthensCamp, invalid.Camp, "invalid camp uses the safe Athens default");
        Check.Equal(GameDefaults.AthensCapitalMap, invalid.CurrentMap, "invalid camp uses the Athens capital");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldSpawnAsync()
    {
        var character = CreateCharacter();
        const uint objectId = 0x6A17C04D;
        var packet = PacketBuilder.PlayerWorldSpawn(character, objectId);

        Check.Equal(300, packet.Length, "PlayerWorldSpawn packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "PlayerWorldSpawn declared length");
        Check.Equal((ushort)0x2725, ReadUInt16(packet, 2), "PlayerWorldSpawn opcode");
        Check.Equal(objectId, ReadUInt32(packet, 4), "PlayerWorldSpawn object id");
        Check.Equal(character.PositionX, ReadSingle(packet, 60), "PlayerWorldSpawn X at offset 60");
        Check.Equal(character.PositionZ, ReadSingle(packet, 64), "PlayerWorldSpawn Z at offset 64");
        Check.Equal(0f, ReadSingle(packet, 68), "PlayerWorldSpawn terrain-height float at offset 68");
        Check.Equal(1f, ReadSingle(packet, 72), "PlayerWorldSpawn facing at offset 72");
        Check.Equal(character.Face, packet[56], "PlayerWorldSpawn face");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldAppearanceAsync()
    {
        var character = CreateAppearanceCharacter();
        var packet = PacketBuilder.PlayerWorldSpawn(character, 0x613u);

        ReadOnlySpan<byte> expectedVisuals = [0xCA, 0xCA, 0xCA, 0x87, 0x11];
        Check.True(
            packet.AsSpan(81, expectedVisuals.Length).SequenceEqual(expectedVisuals),
            "world visual bytes preserve compact item order and grade/quality nibbles");
        Check.True(
            packet.AsSpan(81 + expectedVisuals.Length, 18 - expectedVisuals.Length).IndexOfAnyExcept((byte)0) < 0,
            "unused world visual bytes are zero");

        ReadOnlySpan<byte> expectedAttributeCounts = [4, 5, 5, 2, 0];
        Check.True(
            packet.AsSpan(102, expectedAttributeCounts.Length).SequenceEqual(expectedAttributeCounts),
            "world item attribute counts preserve compact item order");
        Check.True(
            packet.AsSpan(102 + expectedAttributeCounts.Length, 17 - expectedAttributeCounts.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused world item attribute counts are zero");

        ushort[] expectedIds = [2443, 2261, 1834, 14504, 16184];
        for (var index = 0; index < expectedIds.Length; index++)
        {
            Check.Equal(
                expectedIds[index],
                ReadUInt16(packet, 124 + (index * sizeof(ushort))),
                $"world compact equipment id {index}");
        }

        Check.Equal(0x00108409u, ReadUInt32(packet, 168), "world source-slot equipment mask");

        Check.Equal(0x31585747u, ReadUInt32(packet, 260), "world full-visual extension marker");
        ReadOnlySpan<byte> expectedFullQualities = [10, 10, 10, 7, 1];
        ReadOnlySpan<byte> expectedFullGrades = [12, 12, 12, 8, 1];
        Check.True(
            packet.AsSpan(264, expectedFullQualities.Length).SequenceEqual(expectedFullQualities),
            "world extension preserves full quality values");
        Check.True(
            packet.AsSpan(282, expectedFullGrades.Length).SequenceEqual(expectedFullGrades),
            "world extension preserves full grade values");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldExtendedAppearanceAsync()
    {
        var character = CreateCharacter();
        var slots = Enumerable.Repeat("[]", 21).ToArray();
        slots[0] = "[2344,4,80,40,60,240,20,25,1,1,0]";
        slots[8] = "[3246,4,80,240,60,134,20,25,1,1,0]";
        slots[9] = "[3246,4,80,240,60,134,20,25,1,1,0]";
        slots[10] = "[1435,4,80,90,60,230,20,25,1,1,0]";
        character.Equipment = string.Join('#', slots) + '#';

        var packet = PacketBuilder.PlayerWorldSpawn(character, 0x814u);

        ReadOnlySpan<byte> expectedLegacyVisuals = [0xCA, 0xCA, 0xCA, 0xCA];
        Check.True(
            packet.AsSpan(81, expectedLegacyVisuals.Length).SequenceEqual(expectedLegacyVisuals),
            "legacy world decoder retains the captured Q10/G12 projection");
        Check.Equal(0x31585747u, ReadUInt32(packet, 260), "extended world marker is GWX1");

        ReadOnlySpan<byte> expectedFullQualities = [20, 20, 20, 20];
        ReadOnlySpan<byte> expectedFullGrades = [25, 25, 25, 25];
        Check.True(
            packet.AsSpan(264, expectedFullQualities.Length).SequenceEqual(expectedFullQualities),
            "extended world qualities preserve Q20");
        Check.True(
            packet.AsSpan(282, expectedFullGrades.Length).SequenceEqual(expectedFullGrades),
            "extended world grades preserve G25");
        Check.True(
            packet.AsSpan(264 + expectedFullQualities.Length, 18 - expectedFullQualities.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused extended world quality bytes are zero");
        Check.True(
            packet.AsSpan(282 + expectedFullGrades.Length, 18 - expectedFullGrades.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused extended world grade bytes are zero");

        Check.Equal((ushort)3246, ReadUInt16(packet, 126), "first extended ring remains packed");
        Check.Equal((ushort)3246, ReadUInt16(packet, 128), "second extended ring remains packed");
        Check.Equal((byte)5, packet[102], "extended head keeps its real append-attribute count");
        Check.Equal((byte)5, packet[105], "extended weapon keeps its real append-attribute count");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerAuxiliaryAppearanceAsync()
    {
        var character = CreateAppearanceCharacter();
        const uint objectId = 0x716u;

        var refresh = PacketBuilder.EquipmentVisualRefresh(character, objectId);
        Check.Equal(objectId, ReadUInt32(refresh, 4), "EquipmentVisualRefresh object id");
        Check.Equal((uint)character.Hair, ReadUInt32(refresh, 8), "EquipmentVisualRefresh hair/model");
        Check.Equal((uint)character.Gender + 1u, ReadUInt32(refresh, 12), "EquipmentVisualRefresh one-based gender");
        Check.Equal(2443u, ReadUInt32(refresh, 16), "EquipmentVisualRefresh source slot 0");
        Check.Equal(2261u, ReadUInt32(refresh, 28), "EquipmentVisualRefresh source slot 3");
        Check.Equal(1834u, ReadUInt32(refresh, 56), "EquipmentVisualRefresh source slot 10");

        var extras = PacketBuilder.PlayerAppearanceExtras(character, objectId);
        Check.Equal(objectId, ReadUInt32(extras, 8), "PlayerAppearanceExtras object id");
        Check.Equal((byte)1, extras[64], "PlayerAppearanceExtras neutral presence marker");
        for (var offset = 4; offset < extras.Length; offset++)
        {
            if (offset is >= 8 and < 12 || offset == 64)
            {
                continue;
            }

            Check.Equal((byte)0, extras[offset], $"PlayerAppearanceExtras neutral byte {offset}");
        }

        var title = PacketBuilder.PlayerTitleInfo(character, objectId);
        Check.Equal(objectId, ReadUInt32(title, 4), "PlayerTitleInfo object id");
        Check.True(
            title.AsSpan(8).IndexOfAnyExcept((byte)0) < 0,
            "PlayerTitleInfo untitled body is zero");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerInspectExtendedSlotsAsync()
    {
        var character = CreateAppearanceCharacter();
        var packet = PacketBuilder.PlayerInspectEquipment(character, 0x817u);
        const int headerLength = 8;
        const int recordLength = 72;
        const int maskOffset = 1520;

        Check.Equal(1524, packet.Length, "inspect packet includes trailing slot mask");
        Check.Equal(2443u, ReadUInt32(packet, headerLength), "inspect packed record 0 is source slot 0");
        Check.Equal(
            2261u,
            ReadUInt32(packet, headerLength + recordLength),
            "inspect packed record 1 skips empty source slots");
        Check.Equal(
            14504u,
            ReadUInt32(packet, headerLength + (3 * recordLength)),
            "inspect packed cosmetic source slot 15 item");
        Check.Equal(
            16184u,
            ReadUInt32(packet, headerLength + (4 * recordLength)),
            "inspect packed title/cosmetic source slot 20 item");
        Check.Equal(
            uint.MaxValue,
            ReadUInt32(packet, headerLength + (5 * recordLength)),
            "first unused inspect record uses empty sentinel");
        Check.Equal(0x00108409u, ReadUInt32(packet, maskOffset), "inspect source-slot mask");

        var detailedSlots = Enumerable.Repeat("[]", 21).ToArray();
        detailedSlots[0] = "[2344,4,80,40,60,240,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        detailedSlots[8] = "[3246,4,80,240,60,134,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        detailedSlots[9] = "[3246,4,80,240,60,134,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        character.Equipment = string.Join('#', detailedSlots) + '#';

        var detailedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x817u);
        Check.Equal(2344u, ReadUInt32(detailedPacket, headerLength), "inspect detailed head item");
        Check.Equal((byte)20, detailedPacket[headerLength + 24], "inspect preserves Q20");
        Check.Equal((byte)25, detailedPacket[headerLength + 25], "inspect preserves G25");
        var expectedAttributes = new[] { 4, 80, 40, 60, 240 };
        for (var attribute = 0; attribute < expectedAttributes.Length; attribute++)
        {
            Check.Equal(
                expectedAttributes[attribute],
                ReadInt32(detailedPacket, headerLength + 4 + (attribute * 4)),
                $"inspect preserves append attribute {attribute + 1}");
        }

        Check.Equal((ushort)710, ReadUInt16(detailedPacket, headerLength + 32), "inspect preserves holy suit code");
        Check.Equal((ushort)4, ReadUInt16(detailedPacket, headerLength + 34), "inspect preserves socket count");
        var expectedStoneCodes = new ushort[] { 109, 509, 709, 309 };
        var expectedStoneValues = new ushort[] { 1400, 2200, 1200, 1400 };
        for (var stone = 0; stone < expectedStoneCodes.Length; stone++)
        {
            Check.Equal(
                expectedStoneCodes[stone],
                ReadUInt16(detailedPacket, headerLength + 36 + (stone * 2)),
                $"inspect preserves holy-stone code {stone + 1}");
            Check.Equal(
                expectedStoneValues[stone],
                ReadUInt16(detailedPacket, headerLength + 44 + (stone * 2)),
                $"inspect preserves holy-stone value {stone + 1}");
        }

        Check.Equal(3246u, ReadUInt32(detailedPacket, headerLength + recordLength), "inspect first ring record");
        Check.Equal(3246u, ReadUInt32(detailedPacket, headerLength + (2 * recordLength)), "inspect second ring record");
        var expectedRingAttributes = new[] { 4, 80, 240, 60, 134 };
        for (var attribute = 0; attribute < expectedRingAttributes.Length; attribute++)
        {
            Check.Equal(
                expectedRingAttributes[attribute],
                ReadInt32(
                    detailedPacket,
                    headerLength + (2 * recordLength) + 4 + (attribute * 4)),
                $"inspect second ring preserves append attribute {attribute + 1}");
        }

        Check.Equal(
            expectedStoneCodes[3],
            ReadUInt16(detailedPacket, headerLength + (2 * recordLength) + 42),
            "inspect second ring preserves fourth holy stone");
        Check.Equal(0x00000301u, ReadUInt32(detailedPacket, maskOffset), "inspect distinguishes both ring slots");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + recordLength + 64)
                != ReadUInt32(detailedPacket, headerLength + (2 * recordLength) + 64),
            "identical ring types have distinct item state identities");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + recordLength + 68)
                != ReadUInt32(detailedPacket, headerLength + (2 * recordLength) + 68),
            "identical ring types have distinct item slot identities");

        var repeatedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x818u);
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 64),
            ReadUInt32(repeatedPacket, headerLength + 64),
            "inspect item state identity is stable");
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 68),
            ReadUInt32(repeatedPacket, headerLength + 68),
            "inspect item slot identity is stable");

        var otherCharacter = CreateAppearanceCharacter();
        otherCharacter.Id = character.Id + 1;
        otherCharacter.Equipment = character.Equipment;
        var otherPacket = PacketBuilder.PlayerInspectEquipment(otherCharacter, 0x819u);
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 64) != ReadUInt32(otherPacket, headerLength + 64),
            "inspect item state identity is character-specific");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 68) != ReadUInt32(otherPacket, headerLength + 68),
            "inspect item slot identity is character-specific");

        detailedSlots[0] = "[2344,4,80,40,60,240,20,25,1,1,0,711,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        character.Equipment = string.Join('#', detailedSlots) + '#';
        var upgradedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x81Au);
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 64) != ReadUInt32(upgradedPacket, headerLength + 64),
            "inspect item state identity changes with item metadata");
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 68),
            ReadUInt32(upgradedPacket, headerLength + 68),
            "inspect item slot identity survives item metadata changes");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerStatusUpdateAsync()
    {
        var character = CreateCharacter();
        const uint objectId = 0x7135B24E;
        var packet = PacketBuilder.PlayerStatusUpdate(character, objectId);

        Check.Equal(236, packet.Length, "PlayerStatusUpdate packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "PlayerStatusUpdate declared length");
        Check.Equal((ushort)0x27B6, ReadUInt16(packet, 2), "PlayerStatusUpdate opcode");
        Check.Equal(objectId, ReadUInt32(packet, 4), "PlayerStatusUpdate object id");
        Check.Equal(character.Name, ReadFixedAscii(packet, 8, 32), "PlayerStatusUpdate character name");
        Check.Equal(character.Gender, packet[40], "PlayerStatusUpdate gender");
        Check.Equal(character.PositionX, ReadSingle(packet, 44), "PlayerStatusUpdate X at offset 44");
        Check.Equal(0f, ReadSingle(packet, 48), "PlayerStatusUpdate terrain-height float at offset 48");
        Check.Equal(character.PositionZ, ReadSingle(packet, 52), "PlayerStatusUpdate Z at offset 52");
        Check.Equal(1f, ReadSingle(packet, 56), "PlayerStatusUpdate facing at offset 56");
        Check.Equal((int)character.Profession, ReadInt32(packet, 92), "PlayerStatusUpdate profession");
        Check.Equal(character.Level, ReadInt32(packet, 100), "PlayerStatusUpdate level");
        Check.Equal(character.CurrentHp, ReadInt32(packet, 104), "PlayerStatusUpdate current HP");
        Check.Equal(character.CurrentMp, ReadInt32(packet, 108), "PlayerStatusUpdate current MP");
        Check.Equal(character.MaxHp, ReadInt32(packet, 144), "PlayerStatusUpdate max HP");
        Check.Equal(character.MaxMp, ReadInt32(packet, 148), "PlayerStatusUpdate max MP");
        Check.Equal(character.CalculatedStats!.PhysicalAttack, ReadInt32(packet, 152), "PlayerStatusUpdate physical attack");
        Check.Equal(character.CalculatedStats.PhysicalDefense, ReadInt32(packet, 156), "PlayerStatusUpdate physical defense");
        Check.Equal(character.CalculatedStats.MagicAttack, ReadInt32(packet, 168), "PlayerStatusUpdate magic attack");
        Check.Equal(character.CalculatedStats.MagicDefense, ReadInt32(packet, 172), "PlayerStatusUpdate magic defense");
        Check.Equal(character.CalculatedStats.Hit, ReadInt32(packet, 176), "PlayerStatusUpdate hit");
        Check.Equal(character.CalculatedStats.Dodge, ReadInt32(packet, 180), "PlayerStatusUpdate dodge");
        Check.Equal(character.CalculatedStats.Critical, ReadInt32(packet, 184), "PlayerStatusUpdate critical");
        Check.Equal(character.CalculatedStats.CriticalResistance, ReadInt32(packet, 188), "PlayerStatusUpdate critical resistance");
        Check.Equal(character.TalentPoints, ReadInt32(packet, 228), "PlayerStatusUpdate talent points");

        return Task.CompletedTask;
    }

    private static Task CheckNpcDefinitionsAndSpawnLayoutAsync()
    {
        var capturedPacket = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(capturedPacket.AsSpan(0, 2), 108);
        BinaryPrimitives.WriteUInt16LittleEndian(capturedPacket.AsSpan(2, 2), 0x2724);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(4, 4), 0x11);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(8, 4), 5083);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(capturedPacket.AsSpan(24, 4), 1521);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(28, 4), 126f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(32, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(36, 4), -169.9f);
        BinaryPrimitives.WriteSingleLittleEndian(capturedPacket.AsSpan(40, 4), 4.7f);
        Encoding.ASCII.GetBytes("Sparta_086_Male35").CopyTo(capturedPacket, 44);

        var detail10077 = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(detail10077.AsSpan(0, 2), (ushort)detail10077.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(detail10077.AsSpan(2, 2), 10077);
        BinaryPrimitives.WriteUInt32LittleEndian(detail10077.AsSpan(4, 4), 5083);
        var detail10080 = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(detail10080.AsSpan(0, 2), (ushort)detail10080.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(detail10080.AsSpan(2, 2), 10080);
        BinaryPrimitives.WriteUInt32LittleEndian(detail10080.AsSpan(4, 4), 5083);

        var capturedSpartaArtisan = new CapturedNpcSpawn(
            0,
            "Sparta",
            "Sparta_086",
            "Sparta_086_Male35",
            5083,
            126f,
            -169.9f,
            capturedPacket,
            detail10077,
            detail10080);
        var athensDefinitions = NpcSpawnDefinitionFactory.Create(1, [], [capturedSpartaArtisan], []);
        var athensArtisan = athensDefinitions.Single(definition => definition.NpcKey == "Athens_086");
        Check.Equal(5225u, athensArtisan.ObjectId, "Athens artisan object id");
        Check.Equal(5225u, athensArtisan.InteractionId, "Athens artisan interaction id");
        Check.Equal(126f, athensArtisan.X, "Athens artisan paired X");
        Check.Equal(-169.9f, athensArtisan.Z, "Athens artisan paired Z");
        Check.Equal(4.7f, athensArtisan.Facing, "Athens artisan paired facing");
        Check.Equal("Athens_086_Male35", athensArtisan.TemplateKey, "Athens artisan paired template");
        Check.Equal(0, athensArtisan.Detail10077.Length, "Athens fallback does not inherit Sparta detail 10077");
        Check.Equal(0, athensArtisan.Detail10080.Length, "Athens fallback does not inherit Sparta detail 10080");

        var spartaDefinitions = NpcSpawnDefinitionFactory.Create(0, [capturedSpartaArtisan], [], []);
        var spartaArtisan = spartaDefinitions.Single(definition => definition.NpcKey == "Sparta_086");
        Check.Equal(5083u, spartaArtisan.ObjectId, "Sparta artisan object id");
        Check.True(spartaArtisan.Detail10077.SequenceEqual(detail10077), "Sparta detail 10077 is preserved");
        Check.True(spartaArtisan.Detail10080.SequenceEqual(detail10080), "Sparta detail 10080 is preserved");

        var stream = PacketBuilder.NpcSpawns([spartaArtisan, athensArtisan]);
        var athensOffset = 108 + detail10077.Length + detail10080.Length;
        Check.Equal(athensOffset + 108, stream.Length, "authoritative NPC frames include captured details");
        CheckNpcSpawnFrame(stream, 0, spartaArtisan);
        Check.True(
            stream.AsSpan(108, detail10077.Length).SequenceEqual(detail10077),
            "detail 10077 follows captured NPC appearance");
        Check.True(
            stream.AsSpan(108 + detail10077.Length, detail10080.Length).SequenceEqual(detail10080),
            "detail 10080 follows captured NPC appearance");
        CheckNpcSpawnFrame(stream, athensOffset, athensArtisan);
        return Task.CompletedTask;
    }

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

    private static NpcSpawnDefinition CreateNpcDefinition(uint objectId, float x, float z)
    {
        return new NpcSpawnDefinition(
            0,
            "Sparta",
            $"Sparta_Test_{objectId}",
            $"Sparta_Test_{objectId}_Male1",
            objectId,
            x,
            z,
            objectId,
            NpcSpawnDefinitionFactory.DefaultAppearanceType,
            NpcSpawnDefinitionFactory.DefaultFacing,
            [],
            []);
    }

    private static CapturedMonsterSpawn CreateCapturedMonster(
        uint objectId,
        float x,
        float z,
        string templateKey,
        uint objectType = 0x00000212)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectType);
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

    private static async Task CheckMapRegistryWorldReadinessAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var outboundClient = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await outboundClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var inboundClient = await acceptTask;
            await using var session = new ClientSession(outboundClient);

            using var existingOutboundClient = new TcpClient();
            var existingAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await existingOutboundClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var existingInboundClient = await existingAcceptTask;
            await using var existingSession = new ClientSession(existingOutboundClient);

            var character = CreateCharacter();
            var existingCharacter = CreateCharacter();
            existingCharacter.Id += 1;
            existingCharacter.AccountId += 1;
            existingCharacter.Name = "ExistingHero";
            var registry = new GameSessionRegistry();
            registry.JoinMap(
                existingSession,
                existingCharacter.AccountId,
                existingCharacter,
                0x6402);
            Check.Throws<InvalidOperationException>(
                () => registry.JoinMap(session, character.AccountId, character, 0x6402, worldReady: false),
                "map registry rejects duplicate player world object IDs");
            registry.JoinMap(session, character.AccountId, character, 0x6401, worldReady: false);
            Check.Equal(1, registry.GetMapSessions(character.CurrentMap).Count, "not-ready session is hidden from map snapshots");
            Check.True(
                !registry.TryGetMapSessionByObjectId(character.CurrentMap, 0x6401, null, out _),
                "not-ready session is hidden from object lookup");

            Check.True(
                !registry.TryMarkWorldReady(session, new Dictionary<uint, long>(), out var unseenPlayers),
                "activation waits for unseen ready players");
            Check.Equal(1, unseenPlayers.Count, "activation returns the unseen ready player");
            Check.Equal(0x6402u, unseenPlayers[0].ObjectId, "activation returns the correct unseen object");

            var knownWorldRevisions = unseenPlayers.ToDictionary(
                player => player.ObjectId,
                player => player.WorldRevision);
            existingCharacter.Equipment = "[2443,24,90,60,250,,10,12,1,1,0]#";
            registry.UpdateCharacter(existingSession, existingCharacter);
            Check.True(
                !registry.TryMarkWorldReady(session, knownWorldRevisions, out var changedPlayers),
                "activation waits for a player changed during bootstrap");
            Check.Equal(1, changedPlayers.Count, "activation returns the changed ready player");
            Check.True(
                changedPlayers[0].WorldRevision > unseenPlayers[0].WorldRevision,
                "changed player has a newer world revision");

            knownWorldRevisions[changedPlayers[0].ObjectId] = changedPlayers[0].WorldRevision;
            for (var movementIndex = 0; movementIndex < 512; movementIndex++)
            {
                existingCharacter.PositionX += 1f;
                registry.UpdateCharacter(
                    existingSession,
                    existingCharacter,
                    advanceWorldRevision: false);
            }

            Check.True(
                registry.TryMarkWorldReady(
                    session,
                    knownWorldRevisions,
                    out var remainingPlayers),
                "activation succeeds after existing players are known");
            Check.Equal(0, remainingPlayers.Count, "successful activation has no unseen players");
            Check.Equal(2, registry.GetMapSessions(character.CurrentMap).Count, "ready session enters map snapshots");
            Check.True(
                registry.TryGetMapSessionByObjectId(character.CurrentMap, 0x6401, null, out var context) &&
                context.WorldReady,
                "ready session enters object lookup");
            registry.Remove(session);
            registry.Remove(existingSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void CheckNpcSpawnFrame(byte[] stream, int offset, NpcSpawnDefinition definition)
    {
        Check.Equal((ushort)108, ReadUInt16(stream, offset), $"NPC {definition.NpcKey} declared length");
        Check.Equal((ushort)0x2724, ReadUInt16(stream, offset + 2), $"NPC {definition.NpcKey} opcode");
        Check.Equal(definition.AppearanceType, ReadUInt32(stream, offset + 4), $"NPC {definition.NpcKey} appearance type");
        Check.Equal(definition.ObjectId, ReadUInt32(stream, offset + 8), $"NPC {definition.NpcKey} object id");
        Check.Equal(1u, ReadUInt32(stream, offset + 12), $"NPC {definition.NpcKey} active marker");
        Check.Equal(0u, ReadUInt32(stream, offset + 20), $"NPC {definition.NpcKey} neutral field");
        Check.Equal(1521u, ReadUInt32(stream, offset + 24), $"NPC {definition.NpcKey} appearance metadata");
        Check.Equal(definition.X, ReadSingle(stream, offset + 28), $"NPC {definition.NpcKey} X");
        Check.Equal(0f, ReadSingle(stream, offset + 32), $"NPC {definition.NpcKey} Y");
        Check.Equal(definition.Z, ReadSingle(stream, offset + 36), $"NPC {definition.NpcKey} Z");
        Check.Equal(definition.Facing, ReadSingle(stream, offset + 40), $"NPC {definition.NpcKey} facing");
        Check.Equal(
            definition.TemplateKey,
            ReadFixedAscii(stream, offset + 44, 64),
            $"NPC {definition.NpcKey} template");
    }

    private static async Task CheckConcurrentSendOrderingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cancellationToken = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var outboundClient = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            await outboundClient.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            using var inboundClient = await acceptTask;
            await using var session = new ClientSession(outboundClient);

            var clearPackets = Enumerable.Range(0, ConcurrentPacketCount)
                .Select(CreateConcurrentPacket)
                .ToArray();
            var receiveTask = ReadExactlyAsync(
                inboundClient.GetStream(),
                ConcurrentPacketCount * ConcurrentPacketLength,
                cancellationToken);

            var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendTasks = Enumerable.Range(0, ConcurrentPacketCount)
                .Select(packetId => Task.Run(async () =>
                {
                    await startGate.Task;
                    await session.SendAsync(clearPackets[packetId], cancellationToken);
                }, cancellationToken))
                .ToArray();

            startGate.SetResult(true);
            await Task.WhenAll(sendTasks).WaitAsync(cancellationToken);
            var encryptedStream = await receiveTask;

            var receiveCipher = new PacketCipher();
            receiveCipher.Transform(encryptedStream);
            AssertConcurrentFrames(encryptedStream);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static GameCharacter CreateCharacter()
    {
        return new GameCharacter
        {
            Id = 731,
            AccountId = 17,
            Name = "ProtocolHero",
            Gender = 2,
            Camp = 1,
            Profession = 3,
            Hair = 7,
            CurrentMap = 2,
            Level = 177,
            CurrentHp = 123_456,
            CurrentMp = 23_456,
            MaxHp = 234_567,
            MaxMp = 34_567,
            TalentPoints = 456_789,
            PositionX = 321.125f,
            PositionZ = -654.75f,
            CalculatedStats = new CharacterStats
            {
                PhysicalAttack = 91_001,
                PhysicalDefense = 82_002,
                MagicAttack = 73_003,
                MagicDefense = 64_004,
                Hit = 55_005,
                Dodge = 46_006,
                Critical = 37_007,
                CriticalResistance = 28_008
            }
        };
    }

    private static GameCharacter CreateAppearanceCharacter()
    {
        var character = CreateCharacter();
        var slots = Enumerable.Repeat("[]", 21).ToArray();
        slots[0] = "[2443,24,90,60,250,,10,12,1,1,0]";
        slots[3] = "[2261,13,103,133,33,40,10,12,1,1,0]";
        slots[10] = "[1834,24,90,250,60,230,10,12,1,1,0]";
        slots[15] = "[14504,374,414,,,,7,8,1,1,0]";
        slots[20] = "[16184,,,,,,1,1,1,1,0]";
        character.Equipment = string.Join('#', slots) + '#';
        character.Face = 4;
        return character;
    }

    private static byte[] CreateConcurrentPacket(int packetId)
    {
        var packet = new byte[ConcurrentPacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), ConcurrentPacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), ConcurrentPacketOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), packetId);

        for (var offset = 8; offset < packet.Length; offset++)
        {
            packet[offset] = ConcurrentPayloadByte(packetId, offset);
        }

        return packet;
    }

    private static void AssertConcurrentFrames(byte[] clearStream)
    {
        Check.Equal(
            ConcurrentPacketCount * ConcurrentPacketLength,
            clearStream.Length,
            "concurrent send stream length");

        var seenPacketIds = new HashSet<int>();
        for (var frameIndex = 0; frameIndex < ConcurrentPacketCount; frameIndex++)
        {
            var frame = clearStream.AsSpan(
                frameIndex * ConcurrentPacketLength,
                ConcurrentPacketLength);
            Check.Equal(
                (ushort)ConcurrentPacketLength,
                BinaryPrimitives.ReadUInt16LittleEndian(frame),
                $"frame {frameIndex} declared length");
            Check.Equal(
                ConcurrentPacketOpcode,
                BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]),
                $"frame {frameIndex} opcode");

            var packetId = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
            Check.True(
                packetId is >= 0 and < ConcurrentPacketCount,
                $"frame {frameIndex} packet id {packetId} is in range");
            Check.True(seenPacketIds.Add(packetId), $"packet id {packetId} is unique");

            for (var offset = 8; offset < frame.Length; offset++)
            {
                Check.Equal(
                    ConcurrentPayloadByte(packetId, offset),
                    frame[offset],
                    $"frame {frameIndex} packet {packetId} payload byte {offset}");
            }
        }

        Check.Equal(ConcurrentPacketCount, seenPacketIds.Count, "unique concurrent packet count");
    }

    private static byte ConcurrentPayloadByte(int packetId, int offset)
    {
        return (byte)((packetId * 31 + offset * 17 + 0x5A) & 0xFF);
    }

    private static async Task<byte[]> ReadExactlyAsync(
        NetworkStream stream,
        int byteCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[byteCount];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Loopback stream closed after {offset} of {buffer.Length} bytes.");
            }

            offset += read;
        }

        return buffer;
    }

    private static ushort ReadUInt16(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
    }

    private static int ReadInt32(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, 4));
    }

    private static float ReadSingle(byte[] packet, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset, 4));
    }

    private static string ReadFixedAscii(byte[] packet, int offset, int length)
    {
        return Encoding.ASCII.GetString(packet, offset, length).TrimEnd('\0');
    }
}

internal static class Check
{
    public static void Equal<T>(T expected, T actual, string description)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}.");
        }
    }

    public static void True(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {description}.");
        }
    }

    public static void Throws<TException>(Action action, string description)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }
}
