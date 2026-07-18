using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static class PacketBuilder
{
    private const int CharacterNameOffsetInEnterTemplate = 8;
    private const int EnterLevelOffset = 96;
    private const int EnterTalentPointsOffset = 92;
    private const int EnterPlayerObjectIdOffset = 52;
    private const int EnterPositionXOffset = 56;
    private const int EnterPositionYOffset = 60;
    private const int EnterPositionZOffset = 64;
    private const int EnterMaxHpOffset = 68;
    private const int EnterMaxMpOffset = 72;
    private const int EnterCurrentHpOffset = 76;
    private const int EnterCurrentMpOffset = 80;
    private const int EnterEquipmentMaskOffset = 48;
    private const int EnterEquipmentOffset = 104;
    private const int EnterItemRecordLength = 72;
    private const int KitBagPageCount = 4;
    private const int KitBagSlotsPerPage = 24;
    private const int KitBagDetailRecordsPerPacket = 12;
    private const int KitBagDetailHeaderLength = 24;
    private const int KitBagDetailPacketLength = KitBagDetailHeaderLength + (KitBagDetailRecordsPerPacket * EnterItemRecordLength);
    private const int EquipmentItemSnapshotLength = 92;
    private const int PlayerInspectEquipmentHeaderLength = 8;
    private const int PlayerInspectEquipmentRecordCount = 21;
    private const int PlayerInspectEquipmentMaskLength = sizeof(uint);
    private const int PlayerInspectEquipmentMaskOffset =
        PlayerInspectEquipmentHeaderLength + (PlayerInspectEquipmentRecordCount * EnterItemRecordLength);
    private const int PlayerInspectEquipmentLength =
        PlayerInspectEquipmentMaskOffset + PlayerInspectEquipmentMaskLength;
    private const ushort EquipmentItemSnapshotOpcode = 0x2743;
    private const ushort PlayerInspectEquipmentOpcode = 0x2726;
    private const ushort PlayerInspectProfileOpcode = 0x2772;
    private const ushort PlayerInspectCompleteOpcode = 0x2826;
    private const int PlayerInspectProfileLength = 336;
    private const short CapturedWorldVisualQualityCap = 10;
    private const short CapturedWorldVisualGradeCap = 12;
    private const ushort EnterMainOpcode = 0x2723;
    private const ushort KitBagDetailOpcode = 0x2731;
    private const ushort BagItemActionOpcode = 0x2748;
    private const ushort NpcDialogOpenOpcode = 0x2753;
    private const ushort NpcFunctionActionResponseOpcode = 0x2756;
    private const ushort EnterCompleteOpcode = 0x2715;
    private const ushort PlayerWorldSpawnOpcode = 0x2725;
    private const ushort WorldObjectRemoveOpcode = 0x2728;
    private const ushort PlayerDetailOpcode = 0x273B;
    private const ushort TalentRankListOpcode = 0x273A;
    private const ushort TalentSkillUnlockListOpcode = 0x2739;
    private const ushort SkillListOpcode = 0x27D4;
    private const ushort PlayerExtendedStatusOpcode = 0x27B7;
    private const ushort PlayerUnknown10098Opcode = 0x2772;
    private const ushort PlayerStatusUpdateOpcode = 0x27B6;
    private const int PlayerStatusTalentPointsOffset = 228;
    private const ushort PlayerDetailAckOpcode = 0x27DA;
    private const int PlayerWorldVisualFlagsOffset = 81;
    private const int PlayerWorldVisualFlagsLength = 18;
    private const int PlayerWorldAttributeCountsOffset = 102;
    private const int PlayerWorldAttributeCountsLength = 17;
    private const int PlayerWorldEquipmentIdsOffset = 124;
    private const int PlayerWorldEquipmentIdsLength = 18;
    private const int PlayerWorldEquipmentMaskOffset = 168;
    private const short NativeClientHolyStoneSocketCount = 4;
    private const uint LocalPlayerObjectId = 0x00001448;
    private const uint MonsterObjectIdBase = 0x00002700;
    private const uint MonsterAppearanceType = 0x00000212;
    private const int WorldObjectAppearanceLength = 108;
    private const int WorldObjectTemplateOffset = 44;
    private const int WorldObjectTemplateLength = WorldObjectAppearanceLength - WorldObjectTemplateOffset;
    private static readonly byte[] AthensTemplatePrefix = [(byte)'A', (byte)'t', (byte)'h', (byte)'e', (byte)'n', (byte)'s', (byte)'_'];
    private static readonly byte[] SpartaTemplatePrefix = [(byte)'S', (byte)'p', (byte)'a', (byte)'r', (byte)'t', (byte)'a', (byte)'_'];
    private static readonly byte[] ReferencePlayerName = [(byte)'s', (byte)'u', (byte)'s', (byte)'h', (byte)'1'];
    private static readonly byte[] PlayerDetailTemplate =
    [
        0x88, 0x00, 0x3B, 0x27, 0x74, 0x65, 0x73, 0x74, 0x69, 0x6E, 0x67, 0x39, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x25, 0x43, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0xC2, 0xC2, 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x28, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x33, 0x05, 0x00, 0x00, 0x7C, 0x01, 0x00, 0x00, 0x01, 0x00, 0xDC, 0x05,
        0x35, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];
    private static readonly byte[] PlayerStatusUpdateTemplate = Convert.FromHexString(
        "EC00B6271D01000074657374696E6739000000000000000000000000000000000000000000000000" +
        "0100000000002543000000000000C2C20000803F0000000000000000000000000000000000000000" +
        "000000002800000001000000010000000000000001000000330500007C0100000100DC0535000000" +
        "000000000000000000000000000000000000000000000000330500007C010000320000002F000000" +
        "14000000220000000F00000006000000140000001D00000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000001000000DC0500000000000005000000");
    private static readonly byte[] PlayerExtendedStatusTemplate = Convert.FromHexString(
        "5401B727280500000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000330500007C010000320000002F00000046000000220000000F00000006000000" +
        "140000001D0000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000803F000000000000000000000000");
    private static readonly byte[] PlayerWorldSpawnTemplate = Convert.FromHexString(
        "04012527c5030000ed0f000054545454545454540000000000000000000000000000000000000000" +
        "000000008d1900008d190000010066000000033538b53f43fbf79440aa209d400000803f0080bb44" +
        "05111111110000000000000000000000000000000000000004000000000000000000000000000000" +
        "000000003408540b11078c3700000000000000000000000000000000000000000000000000000000" +
        "000000000000000048041000d0070000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000");
    private static readonly byte[] EnterUiBootstrapTemplate = Convert.FromHexString(
        "2e015928302c312c322c342c33382c34342c3230372c362c372c392c31302c31352c31362c31372c" +
        "31382c31392c32302c32312c32322c32372c34322c3537005900a050bb77f4f51900f86c5f5e18f6" +
        "19000300000074f51900f86c5f5e98cc71580000000050f05e5e2905000064000000a00a5b030000" +
        "0000045a8288f4f5190038503b57f8f51900743f59000000c90300000000933f59004c5a8288f4f5" +
        "190038503b5718f61900a4fd1900ccf5190000060000000000001cf6190022b55a000003611540a3" +
        "0a0100060000f86c5f5e5800000040a90a01000961158c57920f7457920f58060000f456920fab8c" +
        "5800f456920fc88c58008c57920fd856920ff456920fd856920f7457920f010600000c57920ff859" +
        "8288bcb5231e40fe1900ab485c00e85982887c000000");
    private static readonly byte[] SkillUiStateTemplate = Convert.FromHexString(
        "a004fd2708070000030000000000000000000000000000000000000000000000000000000000000000000000280701017800000c0c646401020c1c15500c081024138b13ef1333119c187f15780eb814e415b004b004000001000000010000000100000001000000a0b2a5680000000019000000438e0000baac0000820c0000e009000020090000f58400009d2e0100da24010087220100b626010075230100951401003f0d030f0f06e200010000001bd1010000000000000000000000000000000000000000000000000000000000000000000a04020078000001016464010001b50400000000000000000000000000000000000000000000f401f401000001000000010000000100000001000000c9edb20200000000070000007d000000a5000000a70000008a000000e0000000f10000009405000013200000f32e000074140000371e0000570f0000d200050000000000000000001cd1010000000000000000000000000000000000000000000000000000000000000000000a04020078000001016464010001b50400000000000000000000000000000000000000000000f401f4010000010000000100000001000000010000006df87a0100000000070000000a00000097000000930000007400000047000000ff00000095260000e41b000030250000ae1e0000ce0f00008a200000ba00010000000000000000001dd1010000000000000000000000000000000000000000000000000000000000000000000a04020178000001016464010001b50400000000000000000000000000000000000000000000f401f4010000010000000100000001000000010000003cfeb401000000000700000082000000000100002c010000010000001d00000005010000e41b0000331100005e0800009c1f0000f32e0000160c0000aa00020100000000000000001ed1010000000000000000000000000000000000000000000000000000000000000000000a04020178000001026464010001b50400000000000000000000000000000000000000000000f401f401000001000000010000000100000001000000c331e10000000000070000008a0000001600000045000000160100000400000025000000690e0000081a000066220000d21c0000463100001a190000be00010100000000000000001fd1010000000000000000000000000000000000000000000000000000000000000000000a04020078000001036464010001b50400000000000000000000000000000000000000000000f401f401000001000000010000000100000001000000dcdd3d03000000000700000079010000cd0000001b000000cb010000b90100007e000000280b000030250000f9060000371e0000052e000054230000ec000600000000000000000020d1010000000000000000000000000000000000000000000000000000000000000000000a0402006b000001036464010001b50400000000000000000000000000000000000000000000f401f40100000100000001000000010000000100000083fe17003c714300070000000c000000ef0000001802000078000000e9000000200100009c23000068290000161a00006e18000082260000ec2600000e0103010000000000000000");
    private static readonly byte[] CapturedSkillUiStateTemplate = Convert.FromHexString(
        "A802FD270404000039120000000000000000000000000000000000000000000000000000000000000000000027070201780000050564640102043011EF131C157F1500000000000000000000000000000000B004B00400000100000001000000010000000100000035D02B83000000001900000071050000BA0400001F060000AB0600004F0500009C07000006C6000018C50000B3C30000A1C4000047C900003CC30000B1040A0303000200010000003F1D000000000000000000000000000000000000000000000000000000000000000000000407030001000001036464010001D50700000000000000000000000000000000000000000000B004B0040000010000000100000001000000010000007F665108DC05000019000000DD0400002303000086020000F2020000A9030000AE030000000000000000000000000000000000000000000000000000E2010B0000000000010000003E1601000000000000000000000000000000000000000000000000000000000000000000020201011500000101646401000125030000000000000000000000000000000000000000000090019001000001000000010000000100000001000000FA68050031C60800000000006F00000061000000B50000000B01000049000000B7000000E8030000F4010000D002000078000000FC0300005C0300008B0001000000000001000000D1610100000000000000000000000000000000000000000000000000000000000000000001020301010000010364640100019501000000000000000000000000000000000000000000005802580200000100000001000000010000000100000000000000DC05000007000000320000006E00000061000000260000007B00000062000000000000000000000000000000000000000000000000000000540000000000000000000000");
    private static readonly byte[] SkillListBootstrapTemplate = Convert.FromHexString(
        "E400D4271605000012000000C40900000101000000000000140500000001000000000000150500000001000000000000160500000001000000000000BC0B00000201000000000000BE0B00000201000000000000C20B00000201000000000000C30B00000201000000000000C40B00000201000000000000C60B00000201000000000000C70B00000201000000000000B80B00000201000000000000B90B00000201000000000000BD0B00000201000000000000FE1300000401000000000000961300000401000000000000FA1300000401000000000000FB1300000401000000000000");
    private static readonly byte[] CapturedTalentSkillUiStateTemplate = Convert.FromHexString("0800FD2702000000");
    private static readonly byte[] EmptySkillListBootstrapTemplate = Convert.FromHexString("0C00D4270000000000000000");
    private static readonly int[] EnterEquipmentSlots =
    [
        EquipmentSlots.Head,
        EquipmentSlots.Amulet,
        EquipmentSlots.Glove,
        EquipmentSlots.Armor,
        EquipmentSlots.Cuff,
        EquipmentSlots.Girdle,
        EquipmentSlots.Shoes,
        EquipmentSlots.Leggings,
        EquipmentSlots.Ring1,
        EquipmentSlots.Ring2,
        EquipmentSlots.Weapon,
        EquipmentSlots.Shield,
        EquipmentSlots.Stylish
    ];
    // Inspection considers source slots 0..20 in ascending order. Non-empty items
    // are packed into the record array and this source ordering is reconstructed by
    // the trailing slot mask (including cosmetic/title slots 15..20).
    private static readonly int[] InspectEquipmentSlots =
        Enumerable.Range(0, PlayerInspectEquipmentRecordCount).ToArray();
    private static readonly (float X, float Z)[] MonsterSpawnOffsets =
    [
        (10f, 7f),
        (14f, -5f),
        (-11f, 9f),
        (-16f, -7f),
        (20f, 2f),
        (-20f, 2f),
        (7f, 17f),
        (3f, -18f),
    ];

    public static byte[] ServerList()
    {
        return ReferencePackets.ServerList.ToArray();
    }

    public static byte[] SendServer()
    {
        return ReferencePackets.SendServer.ToArray();
    }

    public static byte[] BlankUser()
    {
        return ReferencePackets.BlankUser.ToArray();
    }

    public static byte[] AfterLogin()
    {
        return ReferencePackets.AfterLogin.ToArray();
    }

    public static byte[] ServerTime()
    {
        return ReferencePackets.ServerTime.ToArray();
    }

    public static byte[] AthensNpc(GameCharacter character)
    {
        var stream = ValidPacketStreamPrefix(ReferencePackets.AthensNpc);
        return PatchReferencePlayerPackets(stream, character);
    }

    public static byte[] CityNpcFallback(GameCharacter character)
    {
        if (character.CurrentMap is not (0 or 1))
        {
            return [];
        }

        var packet = AthensNpc(character);
        if (character.CurrentMap == 0)
        {
            ReplaceAscii(packet, AthensTemplatePrefix, SpartaTemplatePrefix);
        }

        return packet;
    }

    public static byte[] CapturedNpcSpawns(IReadOnlyList<CapturedNpcSpawn> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var length = 0;
        foreach (var spawn in spawns)
        {
            length += spawn.Packet.Length;
            length += spawn.Detail10077.Length;
            length += spawn.Detail10080.Length;
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var spawn in spawns)
        {
            spawn.Packet.CopyTo(stream.AsSpan(offset));
            offset += spawn.Packet.Length;
            spawn.Detail10077.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10077.Length;
            spawn.Detail10080.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10080.Length;
        }

        return stream;
    }

    public static byte[] NpcSpawns(IReadOnlyList<NpcSpawnDefinition> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var length = 0;
        foreach (var spawn in spawns)
        {
            length = checked(
                length +
                WorldObjectAppearanceLength +
                spawn.Detail10077.Length +
                spawn.Detail10080.Length);
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var spawn in spawns)
        {
            WriteNpcWorldObjectAppearance(
                stream.AsSpan(offset, WorldObjectAppearanceLength),
                spawn);
            offset += WorldObjectAppearanceLength;
            spawn.Detail10077.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10077.Length;
            spawn.Detail10080.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10080.Length;
        }

        return stream;
    }

    public static byte[] CapturedMonsterSpawns(IReadOnlyList<CapturedMonsterSpawn> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var length = 0;
        foreach (var spawn in spawns)
        {
            length += spawn.Packet.Length;
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var spawn in spawns)
        {
            spawn.Packet.CopyTo(stream.AsSpan(offset));
            offset += spawn.Packet.Length;
        }

        return stream;
    }

    public static int CountCityNpcSpawnPackets(ReadOnlySpan<byte> stream)
    {
        var count = 0;
        var offset = 0;
        while (offset + 4 <= stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            if (length < 4 || offset + length > stream.Length)
            {
                break;
            }

            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(stream.Slice(offset + 2, 2));
            if (opcode == 0x2724)
            {
                count++;
            }

            offset += length;
        }

        return count;
    }

    public static byte[] NearbyMonsterSpawns(GameCharacter character)
    {
        var templates = MonsterTemplateSeeds.Monsters
            .Where(template => template.SourceMapId == character.CurrentMap && !template.IsPet)
            .OrderBy(template => template.IsBoss ? 2 : template.IsElite ? 1 : 0)
            .ThenBy(template => template.TemplateKey, StringComparer.Ordinal)
            .Take(MonsterSpawnOffsets.Length)
            .ToArray();

        if (templates.Length == 0)
        {
            return [];
        }

        var stream = new byte[templates.Length * WorldObjectAppearanceLength];
        for (var i = 0; i < templates.Length; i++)
        {
            var offset = MonsterSpawnOffsets[i % MonsterSpawnOffsets.Length];
            WriteWorldObjectAppearance(
                stream.AsSpan(i * WorldObjectAppearanceLength, WorldObjectAppearanceLength),
                MonsterObjectIdBase + (uint)i,
                templates[i].TemplateKey,
                character.PositionX + offset.X,
                character.PositionZ + offset.Z,
                templates[i].Scale ?? 1.0f);
        }

        return stream;
    }

    public static byte[] EnterPart2Unknown()
    {
        return ReferencePackets.EnterPart2Unknown.ToArray();
    }

    public static byte[] EnterPart2()
    {
        return ReferencePackets.EnterPart2.ToArray();
    }

    public static byte[] EnterPart4()
    {
        return ReferencePackets.EnterPart4.ToArray();
    }

    public static byte[] LoginFailed(ushort reason)
    {
        return
        [
            0x06, 0x00,
            (byte)(reason & 0xFF), (byte)(reason >> 8),
            0x00, 0x00,
            0xF0
        ];
    }

    public static byte[] GameServerRedirect(string host, int port)
    {
        var packet = ReferencePackets.NewGameServerTemplate.ToArray();
        PacketText.WriteFixedAscii(packet.AsSpan(5, Math.Min(23, packet.Length - 5)), host);
        if (packet.Length >= 44)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(40, 4), port);
        }

        return packet;
    }

    public static byte[] CreateRoleSuccess()
    {
        return [0x0C, 0x00, 0xB4, 0x27, 0x13, 0x27, 0x8D, 0x0B, 0x01, 0x00, 0x00, 0x00];
    }

    public static byte[] DeleteRoleSuccess()
    {
        return [0x0C, 0x00, 0xB4, 0x27, 0x14, 0x27, 0xA4, 0x75, 0x08, 0x00, 0x00, 0x00];
    }

    public static byte[] StorageItemUnequipToKitBag(int equipmentSlot, int destinationSlot)
    {
        var packet = new byte[42];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2744);

        // Captured working service ack for equipment -> kitbag unequip.
        // Do not use MSG_MOVE_ITEM here; this client closes when it receives that path.
        var destinationPage = Math.DivRem(destinationSlot, 24, out var destinationIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), (ushort)equipmentSlot);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), (ushort)destinationPage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)destinationIndex);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), -1);
        return packet;
    }

    public static byte[] StorageItemEquipFromKitBag(int sourceSlot, int clientEquipmentSlot)
    {
        var packet = new byte[42];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2744);

        var sourcePage = Math.DivRem(sourceSlot, 24, out var sourceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), (ushort)sourcePage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), (ushort)sourceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)clientEquipmentSlot);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), -1);
        return packet;
    }

    public static byte[] StorageItemKitBagMove(int sourceSlot, int destinationSlot)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2744);

        var sourcePage = Math.DivRem(sourceSlot, 24, out var sourceIndex);
        var destinationPage = Math.DivRem(destinationSlot, 24, out var destinationIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), (ushort)sourcePage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), (ushort)sourceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), (ushort)destinationPage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)destinationIndex);
        return packet;
    }

    public static byte[] BagItemActionAck(ReadOnlySpan<byte> requestPacket)
    {
        const int packetLength = 40;
        var packet = new byte[packetLength];

        if (requestPacket.Length >= packetLength)
        {
            requestPacket[..packetLength].CopyTo(packet);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2748);
        return packet;
    }

    public static byte[] TalentUpgradeAck(TalentUpgradeResult result)
    {
        const int packetLength = 28;
        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2741);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), result.TalentId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), result.NewRank);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), result.Cost);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), result.RemainingTalentPoints);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24, 4), result.DisplayValue);
        return packet;
    }

    public static byte[] TalentRankList(IReadOnlyList<TalentState> talents)
    {
        if (talents.Count == 0)
        {
            return [];
        }

        const int headerLength = 12;
        const int recordLength = 16;
        var packet = new byte[headerLength + (talents.Count * recordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), TalentRankListOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), talents.Count);

        for (var i = 0; i < talents.Count; i++)
        {
            var offset = headerLength + (i * recordLength);
            var talent = talents[i];
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, 4), talent.TalentId);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 4, 4), talent.Rank);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 8, 4), talent.DisplayValue);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 12, 4), talent.NextCost);
        }

        return packet;
    }

    public static byte[] NpcDialogOpenAck(uint npcId, int dialogIndex, string scriptKey)
    {
        var packet = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), NpcDialogOpenOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), dialogIndex);
        PacketText.WriteFixedAscii(packet.AsSpan(16, 32), scriptKey);
        return packet;
    }

    public static byte[] NpcFunctionActionResponse(uint npcId, int dialogIndex, params int[] subIds)
    {
        var packet = new byte[12 + (subIds.Length * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), NpcFunctionActionResponseOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), dialogIndex);

        for (var i = 0; i < subIds.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12 + (i * 4), 4), subIds[i]);
        }

        return packet;
    }

    public static byte[] TalentSkillUnlockList(IReadOnlyList<SkillState> skills)
    {
        if (skills.Count == 0)
        {
            return [];
        }

        const int headerLength = 12;
        const int recordLength = 8;
        var packet = new byte[headerLength + (skills.Count * recordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), TalentSkillUnlockListOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), skills.Count);

        for (var i = 0; i < skills.Count; i++)
        {
            var offset = headerLength + (i * recordLength);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, 4), skills[i].SkillId);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 4, 4), 0);
        }

        return packet;
    }

    public static byte[] ChampionTalentSkillUnlockList()
    {
        var packet = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), TalentSkillUnlockListOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), 250);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), 3062);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24, 4), 0);
        return packet;
    }

    public static byte[] SkillList(IReadOnlyList<SkillState> skills)
    {
        if (skills.Count == 0)
        {
            return [];
        }

        const int headerLength = 12;
        const int recordLength = 12;
        var packet = new byte[headerLength + (skills.Count * recordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), SkillListOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), skills.Count);

        for (var i = 0; i < skills.Count; i++)
        {
            var offset = headerLength + (i * recordLength);
            var skill = skills[i];
            var levelFlag = 0x100 | Math.Clamp(skill.Level, 1, 255);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, 4), skill.SkillId);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 4, 4), levelFlag);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 8, 4), 0);
        }

        return packet;
    }

    public static byte[] EquipmentVisualRefresh(GameCharacter character)
    {
        return EquipmentVisualRefresh(character, LocalPlayerObjectId);
    }

    public static byte[] EquipmentVisualRefresh(GameCharacter character, uint objectId)
    {
        var packet = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D9);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        // Captured 0x27D9 packets carry the avatar hair/model byte followed by
        // the one-based gender, not a constant hair id and profession.
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), character.Hair);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)character.Gender + 1u);

        var equipment = ParseEquipment(character);
        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Shield; slot++)
        {
            var itemId = slot < equipment.Length ? equipment[slot].Id : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16 + (slot * 4), 4), itemId);
        }

        return packet;
    }

    public static byte[] PlayerWorldSpawn(GameCharacter character, uint objectId)
    {
        var packet = PlayerWorldSpawnTemplate.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerWorldSpawnOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)Math.Max(character.Id, 0));
        PacketText.WriteFixedAscii(packet.AsSpan(12, 32), character.Name);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(44, 4), Math.Max(1, character.CurrentHp));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(48, 4), Math.Max(1, character.MaxHp));
        packet[52] = character.Gender;
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(54, 2),
            (ushort)Math.Clamp(character.Level, 1, ushort.MaxValue));
        packet[56] = character.Face;
        packet[58] = ToWorldProfessionByte(character.Profession);
        packet[59] = character.Hair;
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(60, 4), character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(64, 4), character.PositionZ);
        // The third captured coordinate is terrain height. It is not persisted by
        // GameCharacter, so use the neutral value rather than shifting Z into it.
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(68, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(72, 4), 1f);
        PatchPlayerWorldAppearance(packet, character);
        return packet;
    }

    public static byte[] PlayerAppearanceExtras(GameCharacter character, uint objectId)
    {
        var packet = new byte[108];

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2808);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), objectId);
        // Working captures identify the remaining fields as character-specific
        // guild/social appearance data (including an optional name at offset 32),
        // not equipment-aura constants. GameCharacter does not model that data;
        // emit the captured neutral form instead of inventing another player's ids.
        packet[64] = 1;
        return packet;
    }

    public static byte[] PlayerTitleInfo(GameCharacter character, uint objectId)
    {
        var packet = new byte[80];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D7);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        // Offset 8 is the selected title text and offset 76 is a title id in the
        // working captures. Neither is the character id. Until titles are modeled,
        // the all-zero untitled body is the only truthful representation.
        return packet;
    }

    public static byte[] PlayerWorldMovement(ReadOnlySpan<byte> clientWalkPacket, uint objectId)
    {
        var packet = clientWalkPacket.ToArray();
        if (packet.Length < 8)
        {
            return packet;
        }

        var clientMovementState = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        var serverMovementState = (clientMovementState & 0xFFFF0000) | (objectId & 0xFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), serverMovementState);
        return packet;
    }

    public static byte[] SkillCastVisual(ReadOnlySpan<byte> clientSkillCastPacket, uint objectId)
    {
        var packet = clientSkillCastPacket.ToArray();
        if (packet.Length < 8)
        {
            return packet;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2738);
        PatchSkillCastObjectId(packet, 4, objectId);
        PatchSkillCastObjectId(packet, 16, objectId);
        if (packet.Length >= 16
            && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4)) == LocalPlayerObjectId)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), objectId);
        }

        return packet;
    }

    public static byte[] SkillCastImpact(ReadOnlySpan<byte> clientSkillCastPacket, uint objectId)
    {
        var packet = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x273E);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), objectId);

        if (clientSkillCastPacket.Length >= 12)
        {
            clientSkillCastPacket.Slice(8, 4).CopyTo(packet.AsSpan(12, 4));
        }

        if (clientSkillCastPacket.Length >= 32)
        {
            clientSkillCastPacket.Slice(24, 8).CopyTo(packet.AsSpan(16, 8));
        }

        return packet;
    }

    public static byte[] PlayerWorldPosition(GameCharacter character, uint objectId)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x27D2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0x00020000 | (objectId & 0xFFFF));
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8, 4), character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12, 4), character.PositionZ);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16, 4), 1f);
        return packet;
    }

    private static void PatchSkillCastObjectId(byte[] packet, int offset, uint objectId)
    {
        if (packet.Length < offset + 4)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), objectId);
    }

    public static byte[] RemoveWorldObjects(params uint[] objectIds)
    {
        var packet = new byte[8 + (Math.Max(0, objectIds.Length) * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), WorldObjectRemoveOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)objectIds.Length);

        for (var i = 0; i < objectIds.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8 + (i * 4), 4), objectIds[i]);
        }

        return packet;
    }

    public static byte[] StorageMarker(ushort markerOpcode)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2727);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), markerOpcode);
        return packet;
    }

    public static byte[] EquipmentItemSnapshot(GameCharacter character)
    {
        return EquipmentItemSnapshot(character, LocalPlayerObjectId);
    }

    public static byte[] PlayerInspectEquipment(GameCharacter character, uint objectId)
    {
        var packet = new byte[PlayerInspectEquipmentLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerInspectEquipmentOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);

        // Captured 0x2726 responses contain a compact sequence of non-empty item
        // records. Their original source slots are carried separately in the mask
        // at offset 1520; record index is not the equipment slot.
        var items = EquipmentItemsForInspect(character)
            .Where(entry => !entry.Item.IsEmpty)
            .Take(PlayerInspectEquipmentRecordCount)
            .ToArray();
        uint equipmentMask = 0;
        for (var record = 0; record < PlayerInspectEquipmentRecordCount; record++)
        {
            var entry = record < items.Length ? items[record] : default;
            WriteInspectItemRecord(
                packet.AsSpan(
                    PlayerInspectEquipmentHeaderLength + (record * EnterItemRecordLength),
                    EnterItemRecordLength),
                entry.Item,
                character.Id,
                entry.Slot);

            if (!entry.Item.IsEmpty && entry.Slot is >= 0 and < sizeof(uint) * 8)
            {
                equipmentMask |= 1u << entry.Slot;
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerInspectEquipmentMaskOffset, PlayerInspectEquipmentMaskLength),
            equipmentMask);

        return packet;
    }

    public static byte[] PlayerInspectEquipmentStatusBundle(GameCharacter character, uint objectId)
    {
        var inspectEquipment = PlayerInspectEquipment(character, objectId);
        var inspectStatus = PlayerStatusUpdate(character, objectId);
        var bundle = new byte[inspectEquipment.Length + inspectStatus.Length];
        inspectEquipment.CopyTo(bundle, 0);
        inspectStatus.CopyTo(bundle, inspectEquipment.Length);
        return bundle;
    }

    public static byte[] PlayerInspectProfile(GameCharacter character, uint objectId)
    {
        var packet = new byte[PlayerInspectProfileLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerInspectProfileOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        return packet;
    }

    public static byte[] PlayerInspectComplete()
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerInspectCompleteOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0x00000708);
        return packet;
    }

    public static byte[] EquipmentItemSnapshot(GameCharacter character, uint objectId)
    {
        var items = EquipmentItemsBySlot(character)
            .Where(entry => entry.Item is { IsEmpty: false })
            .ToArray();

        if (items.Length == 0)
        {
            return [];
        }

        using var stream = new MemoryStream(items.Length * EquipmentItemSnapshotLength);
        foreach (var (slot, item) in items)
        {
            var packet = EquipmentItemSnapshot(slot, item, objectId);
            stream.Write(packet);
        }

        return stream.ToArray();
    }

    public static byte[] EquipmentItemSnapshot(GameCharacter character, int slot)
    {
        return EquipmentItemSnapshot(character, slot, LocalPlayerObjectId);
    }

    public static byte[] EquipmentItemSnapshot(GameCharacter character, int slot, uint objectId)
    {
        if (!EquipmentSlots.IsEquipmentSlot(slot))
        {
            return [];
        }

        var item = EquipmentSlots.GetItem(EquipmentFor(character), character.Profession, slot);
        return item.IsEmpty ? [] : EquipmentItemSnapshot(slot, item, objectId);
    }

    public static byte[] EquipmentItemEquipSnapshot(GameCharacter character, int sourceSlot, int equippedSlot)
    {
        if (!EquipmentSlots.IsEquipmentSlot(equippedSlot))
        {
            return [];
        }

        var item = EquipmentSlots.GetItem(EquipmentFor(character), character.Profession, equippedSlot);
        if (item.IsEmpty)
        {
            return [];
        }

        var packet = EquipmentItemSnapshot(sourceSlot, item, LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 0);
        var sourcePage = Math.DivRem(Math.Max(sourceSlot, 0), 24, out var sourceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), (ushort)sourcePage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)sourceIndex);
        return packet;
    }

    public static byte[] KitBagItemSnapshot(GameCharacter character, int sourceSlot)
    {
        if (sourceSlot is < 0 or >= KitBagPageCount * KitBagSlotsPerPage)
        {
            return [];
        }

        var item = KitBagSlots.GetItem(
            string.IsNullOrWhiteSpace(character.KitBag) ? GameDefaults.DefaultKitBag : character.KitBag,
            sourceSlot);
        var packet = EquipmentItemSnapshot(sourceSlot, item, LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 0);
        var sourcePage = Math.DivRem(sourceSlot, KitBagSlotsPerPage, out var sourceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), (ushort)sourcePage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)sourceIndex);
        return packet;
    }

    public static byte[] EquipmentItemClearSnapshot(int slot)
    {
        return EquipmentItemClearSnapshot(slot, LocalPlayerObjectId);
    }

    public static byte[] EquipmentItemClearSnapshot(int slot, uint objectId)
    {
        if (!EquipmentSlots.IsEquipmentSlot(slot))
        {
            return [];
        }

        return EquipmentItemSnapshot(slot, CompactItemEntry.Empty, objectId);
    }

    public static byte[] EquipmentItemClearSnapshots(uint objectId)
    {
        using var stream = new MemoryStream((EquipmentSlots.Stylish + 1) * EquipmentItemSnapshotLength);
        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Stylish; slot++)
        {
            stream.Write(EquipmentItemClearSnapshot(slot, objectId));
        }

        return stream.ToArray();
    }

    private static byte[] EquipmentItemSnapshot(int slot, CompactItemEntry item, uint objectId)
    {
        var packet = new byte[EquipmentItemSnapshotLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), EquipmentItemSnapshotLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), EquipmentItemSnapshotOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)slot);
        WriteSnapshotItemRecord(packet.AsSpan(20, EnterItemRecordLength), item);
        return packet;
    }

    public static byte[] PlayerDetail(GameCharacter character)
    {
        var packet = PlayerDetailTemplate.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerDetailOpcode);
        PatchReferencePlayerPacket(packet, character, nameOffset: 4);
        return packet;
    }

    public static byte[] PlayerStatusUpdate(GameCharacter character)
    {
        return PlayerStatusUpdate(character, LocalPlayerObjectId);
    }

    public static byte[] PlayerStatusUpdate(GameCharacter character, uint objectId)
    {
        var packet = PlayerStatusUpdateTemplate.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerStatusUpdateOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        PatchReferencePlayerPacket(packet, character, nameOffset: 8);
        if (packet.Length >= PlayerStatusTalentPointsOffset + 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(PlayerStatusTalentPointsOffset, 4), character.TalentPoints);
        }

        return packet;
    }

    public static byte[] PlayerExtendedStatus(GameCharacter character)
    {
        var packet = PlayerExtendedStatusTemplate.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerExtendedStatusOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        return packet;
    }

    public static byte[] PlayerUnknown10098(int value)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerUnknown10098Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), value);
        return packet;
    }

    public static byte[] PlayerDetailAck(ReadOnlySpan<byte> requestPayload)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerDetailAckOpcode);
        if (requestPayload.Length >= 4)
        {
            requestPayload[..4].CopyTo(packet.AsSpan(4, 4));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        }

        return packet;
    }

    public static byte[] PlayerDetailRefreshAck()
    {
        return PlayerDetailRefreshAck(LocalPlayerObjectId);
    }

    public static byte[] PlayerDetailRefreshAck(uint objectId)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerDetailAckOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 1);
        return packet;
    }

    public static byte[] SelfInfoRefresh(GameCharacter character)
    {
        return EnterMain(character);
    }

    public static int ToClientEquipmentSlot(int equipmentSlot)
    {
        return equipmentSlot;
    }

    public static byte[] CharacterPreview(GameCharacter character)
    {
        var equipmentIds = ParseEquipmentIds(EquipmentFor(character));
        var payloadLength = 32 + 7 + (equipmentIds.Length * 4) + 48;
        var packet = new byte[payloadLength + 5];

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2712);
        packet[4] = 0x01;

        var offset = 5;
        PacketText.WriteFixedAscii(packet.AsSpan(offset, 32), character.Name);
        offset += 32;

        packet[offset++] = character.Camp;
        packet[offset++] = ToClientProfessionByte(character.Profession);
        packet[offset++] = (byte)Math.Clamp(character.Level, 1, 255);
        packet[offset++] = character.Gender;
        packet[offset++] = character.Hair;
        packet[offset++] = character.Face;
        packet[offset++] = 0;

        foreach (var itemId in equipmentIds)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), itemId);
            offset += 4;
        }

        return packet;
    }

    public static byte[] EnterPart1(GameCharacter character)
    {
        return EnterStart(character).Part1;
    }

    public static byte[] EnterMain(GameCharacter character)
    {
        var header = CreateEnterPart1Header(character);
        var continuation = ReferencePackets.EnterPart2Unknown;
        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        var packet = new byte[declaredLength];

        header.CopyTo(packet.AsSpan(0, Math.Min(header.Length, packet.Length)));
        var continuationLength = Math.Min(packet.Length - header.Length, continuation.Length);
        continuation[..continuationLength].CopyTo(packet.AsSpan(header.Length, continuationLength));

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), EnterMainOpcode);
        PatchEnterEquipment(packet, character);
        return packet;
    }

    public static byte[] EnterUiBootstrap()
    {
        return EnterUiBootstrapTemplate.ToArray();
    }

    public static byte[] SkillUiState()
    {
        return CapturedTalentSkillUiStateTemplate.ToArray();
    }

    public static byte[] SkillListBootstrap()
    {
        return EmptySkillListBootstrapTemplate.ToArray();
    }

    public static byte[][] KitBagDetailPages(GameCharacter character)
    {
        var kitBag = KitBagItems(character);
        var packets = new List<byte[]>(KitBagPageCount * 2);

        for (var page = 0; page < KitBagPageCount; page++)
        {
            for (var half = 0; half < 2; half++)
            {
                var packet = new byte[KitBagDetailPacketLength];
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), KitBagDetailPacketLength);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), KitBagDetailOpcode);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 4);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), 0);
                packet[16] = (byte)page;
                packet[17] = (byte)(half * KitBagDetailRecordsPerPacket);
                packet[18] = 0x58;
                packet[19] = 0x00;
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), 0);

                var firstSlot = (page * KitBagSlotsPerPage) + (half * KitBagDetailRecordsPerPacket);
                for (var record = 0; record < KitBagDetailRecordsPerPacket; record++)
                {
                    var slot = firstSlot + record;
                    var item = slot < kitBag.Length ? kitBag[slot] : default;
                    WriteKitBagItemRecord(packet.AsSpan(KitBagDetailHeaderLength + (record * EnterItemRecordLength), EnterItemRecordLength), item);
                }

                packets.Add(packet);
            }
        }

        return packets.ToArray();
    }

    public static byte[][] KitBagSlotIndexes(GameCharacter character)
    {
        const int packetLength = 40;
        var kitBag = KitBagItems(character);
        var packets = new List<byte[]>(KitBagPageCount * KitBagSlotsPerPage);

        for (var page = 0; page < KitBagPageCount; page++)
        {
            for (var index = 0; index < KitBagSlotsPerPage; index++)
            {
                var slot = (page * KitBagSlotsPerPage) + index;
                var item = slot < kitBag.Length ? kitBag[slot] : default;
                var packet = new byte[packetLength];
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), packetLength);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), BagItemActionOpcode);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), -1);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)page);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16, 4), (uint)index);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), item.IsEmpty ? -1 : unchecked((int)item.Id));
                packets.Add(packet);
            }
        }

        return packets.ToArray();
    }

    public static byte[] EnterComplete()
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), EnterCompleteOpcode);
        return packet;
    }

    public static (byte[] Part1, byte[] Part2Unknown) EnterStart(GameCharacter character)
    {
        var part1 = CreateEnterPart1Header(character);
        var part2Unknown = ReferencePackets.EnterPart2Unknown.ToArray();
        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(part1.AsSpan(0, 2));
        var continuationLength = Math.Min(declaredLength - part1.Length, part2Unknown.Length);

        var combined = new byte[part1.Length + continuationLength];
        part1.CopyTo(combined.AsSpan(0, part1.Length));
        part2Unknown.AsSpan(0, continuationLength).CopyTo(combined.AsSpan(part1.Length));

        PatchEnterEquipment(combined, character);

        combined.AsSpan(0, part1.Length).CopyTo(part1);
        combined.AsSpan(part1.Length, continuationLength).CopyTo(part2Unknown);
        return (part1, part2Unknown);
    }

    private static byte[] CreateEnterPart1Header(GameCharacter character)
    {
        var packet = ReferencePackets.EnterPart1.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(EnterPlayerObjectIdOffset, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(EnterPositionXOffset, 4), character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(EnterPositionYOffset, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(EnterPositionZOffset, 4), character.PositionZ);
        // The client renders the current fields first and the max fields second.
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterMaxHpOffset, 4), character.MaxHp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterMaxMpOffset, 4), character.MaxMp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterCurrentHpOffset, 4), character.CurrentHp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterCurrentMpOffset, 4), character.CurrentMp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterLevelOffset, 4), character.Level);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterTalentPointsOffset, 4), character.TalentPoints);
        PacketText.WriteFixedAscii(packet.AsSpan(CharacterNameOffsetInEnterTemplate, 32), character.Name);

        var offset = CharacterNameOffsetInEnterTemplate + 32;
        if (packet.Length >= offset + 8)
        {
            packet[offset++] = character.Gender;
            packet[offset++] = character.Camp;
            packet[offset++] = character.Faith;
            packet[offset++] = ToClientProfessionByte(character.Profession);
            packet[offset++] = character.Hair;
            packet[offset++] = character.Face;
            packet[offset++] = character.CurrentMap;
            packet[offset] = 0;
        }

        return packet;
    }

    private static uint[] ParseEquipmentIds(string equipment)
    {
        return equipment.Split('#', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseEquipmentId)
            .ToArray();
    }

    private static uint ParseEquipmentId(string entry)
    {
        if (entry == "[]")
        {
            return uint.MaxValue;
        }

        var clean = entry.Trim('[', ']');
        var idText = clean.Split(',', 2)[0];
        return uint.TryParse(idText, out var id) ? id : uint.MaxValue;
    }

    public static string EnterEquipmentSummary(GameCharacter character)
    {
        return string.Join(
            ",",
            EquipmentItemsBySlot(character)
                .Where(entry => !entry.Item.IsEmpty)
                .Select(entry =>
                {
                    var suit = entry.Item.HolySuitCode > 0 ? $":s{entry.Item.HolySuitCode}:xp{entry.Item.Exp}" : string.Empty;
                    return $"{entry.Slot}:{entry.Item.Id}:q{entry.Item.Quality}:g{entry.Item.Grade}{suit}";
                }));
    }

    private static void PatchEnterEquipment(byte[] packet, GameCharacter character)
    {
        var items = EnterEquipmentSlots
            .Select(slot =>
            {
                var item = EquipmentSlots.GetItem(EquipmentFor(character), character.Profession, slot);
                return (Slot: slot, Item: item);
            })
            .Where(entry => !entry.Item.IsEmpty)
            .ToArray();
        var availableRecords = Math.Max(0, (packet.Length - EnterEquipmentOffset) / EnterItemRecordLength);
        var equipmentMask = 0;
        for (var i = 0; i < availableRecords; i++)
        {
            var offset = EnterEquipmentOffset + (i * EnterItemRecordLength);
            packet.AsSpan(offset, EnterItemRecordLength).Clear();
        }

        for (var i = 0; i < items.Length && i < availableRecords; i++)
        {
            var offset = EnterEquipmentOffset + (i * EnterItemRecordLength);
            equipmentMask |= 1 << items[i].Slot;
            WriteEnterItemRecord(packet.AsSpan(offset, EnterItemRecordLength), items[i].Item);
        }

        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(EnterEquipmentMaskOffset, 4), equipmentMask);
    }

    private static CompactItemEntry[] EnterEquipmentItems(GameCharacter character)
    {
        var equipment = ParseEquipment(character);

        return EnterEquipmentSlots
            .Select(slot => slot < equipment.Length ? equipment[slot] : default)
            .ToArray();
    }

    private static (int Slot, CompactItemEntry Item)[] EquipmentItemsBySlot(GameCharacter character)
    {
        var equipment = ParseEquipment(character);
        return Enumerable.Range(0, Math.Min(equipment.Length, EquipmentSlots.Stylish + 1))
            .Select(slot => (Slot: slot, Item: equipment[slot]))
            .ToArray();
    }

    private static (int Slot, CompactItemEntry Item)[] PlayerWorldEquipmentItems(GameCharacter character)
    {
        var equipment = ParseEquipment(character);
        return Enumerable.Range(0, equipment.Length)
            .Select(slot => (Slot: slot, Item: equipment[slot]))
            .ToArray();
    }

    private static (int Slot, CompactItemEntry Item)[] EquipmentItemsForInspect(GameCharacter character)
    {
        var equipment = ParseEquipment(character);
        return InspectEquipmentSlots
            .Select(slot => (Slot: slot, Item: slot < equipment.Length ? equipment[slot] : default))
            .ToArray();
    }

    private static CompactItemEntry[] ParseEquipment(GameCharacter character)
    {
        return EquipmentFor(character)
            .Split('#', StringSplitOptions.RemoveEmptyEntries)
            .Select(CompactItemEntry.Parse)
            .ToArray();
    }

    private static void PatchPlayerWorldAppearance(byte[] packet, GameCharacter character)
    {
        packet.AsSpan(PlayerWorldVisualFlagsOffset, PlayerWorldVisualFlagsLength).Clear();
        packet.AsSpan(PlayerWorldAttributeCountsOffset, PlayerWorldAttributeCountsLength).Clear();
        packet.AsSpan(PlayerWorldEquipmentIdsOffset, PlayerWorldEquipmentIdsLength * 2).Clear();

        var visualIndex = 0;
        var equipmentMask = 0u;
        foreach (var (slot, item) in PlayerWorldEquipmentItems(character))
        {
            if (item.IsEmpty || visualIndex >= PlayerWorldEquipmentIdsLength)
            {
                continue;
            }

            if (slot < sizeof(uint) * 8)
            {
                equipmentMask |= 1u << slot;
            }

            packet[PlayerWorldVisualFlagsOffset + visualIndex] = PackWorldItemVisual(item);
            if (visualIndex < PlayerWorldAttributeCountsLength)
            {
                packet[PlayerWorldAttributeCountsOffset + visualIndex] = WorldItemAttributeCount(item);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(PlayerWorldEquipmentIdsOffset + (visualIndex * 2), 2),
                (ushort)Math.Min(item.Id, ushort.MaxValue));
            visualIndex++;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerWorldEquipmentMaskOffset, sizeof(uint)),
            equipmentMask);
    }

    private static byte PackWorldItemVisual(CompactItemEntry item)
    {
        // Captures pair each equipment id with (grade << 4) | quality. In
        // particular, every captured G12/Q10 item is 0xCA regardless of slot.
        var grade = (int)Math.Clamp(item.Grade, (short)0, CapturedWorldVisualGradeCap);
        var quality = (int)Math.Clamp(item.Quality, (short)0, CapturedWorldVisualQualityCap);
        return (byte)((grade << 4) | quality);
    }

    private static byte WorldItemAttributeCount(CompactItemEntry item)
    {
        var count = 0;
        count += HasWorldItemAttribute(item.Attribute1) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute2) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute3) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute4) ? 1 : 0;
        count += HasWorldItemAttribute(item.Attribute5) ? 1 : 0;
        return (byte)count;
    }

    private static bool HasWorldItemAttribute(int? attribute)
    {
        // Captured item records use -1 as the absent sentinel; compact records use null.
        return attribute is >= 0;
    }

    private static CompactItemEntry[] KitBagItems(GameCharacter character)
    {
        var kitBag = string.IsNullOrWhiteSpace(character.KitBag)
            ? GameDefaults.DefaultKitBag
            : character.KitBag;

        var slots = kitBag
            .Split('#', StringSplitOptions.RemoveEmptyEntries)
            .Select(CompactItemEntry.Parse)
            .ToList();

        while (slots.Count < KitBagPageCount * KitBagSlotsPerPage)
        {
            slots.Add(default);
        }

        return slots.Take(KitBagPageCount * KitBagSlotsPerPage).ToArray();
    }

    private static void WriteEnterItemRecord(Span<byte> record, CompactItemEntry item)
    {
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[..4], item.Id);
        WriteNullableInt32(record.Slice(4, 4), item.Attribute1);
        WriteNullableInt32(record.Slice(8, 4), item.Attribute2);
        WriteNullableInt32(record.Slice(12, 4), item.Attribute3);
        WriteNullableInt32(record.Slice(16, 4), item.Attribute4);
        WriteNullableInt32(record.Slice(20, 4), item.Attribute5);
        record[24] = ClampByte(item.Quality);
        record[25] = ClampByte(item.Grade);
        record[26] = ClampByte(item.Bound);
        record[27] = ClampByte(item.Stack);
        WriteItemExtension(record, item);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(68, 4), 0x42);
    }

    private static void WriteKitBagItemRecord(Span<byte> record, CompactItemEntry item, uint ownerObjectId = LocalPlayerObjectId)
    {
        record.Clear();

        if (item.IsEmpty)
        {
            for (var offset = 0; offset <= 20; offset += 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(offset, 4), -1);
            }

            record[24] = 1;
            record[25] = 1;
            record[26] = 0;
            record[27] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(28, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(32, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(68, 4), -1);
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record[..4], item.Id);
        WriteNullableInt32(record.Slice(4, 4), item.Attribute1);
        WriteNullableInt32(record.Slice(8, 4), item.Attribute2);
        WriteNullableInt32(record.Slice(12, 4), item.Attribute3);
        WriteNullableInt32(record.Slice(16, 4), item.Attribute4);
        WriteNullableInt32(record.Slice(20, 4), item.Attribute5);
        record[24] = ClampByte(item.Quality);
        record[25] = ClampByte(item.Grade);
        record[26] = ClampByte(item.Bound);
        record[27] = ClampByte(item.Stack);
        WriteItemExtension(record, item);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(68, 4), ownerObjectId);
    }

    private static void WriteInspectItemRecord(
        Span<byte> record,
        CompactItemEntry item,
        int characterId,
        int sourceSlot)
    {
        // Unlike the one-byte world-appearance summary, an inspect record has a
        // full byte each for quality and grade. Preserve the server values here;
        // the patched client data currently supports Q20/G25.
        WriteKitBagItemRecord(record, item);
        if (item.IsEmpty)
        {
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(64, 4), -1);
            return;
        }

        // Working-server captures keep both tail identifiers stable for a given
        // item across sessions. Reusing record-index identifiers across every
        // character lets the client cache one player's item details for another.
        // Build stable identities from the persistent character/source slot and
        // the complete item state so an upgrade also invalidates stale details.
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(64, 4),
            InspectItemStateIdentity(characterId, sourceSlot, item));
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(68, 4),
            InspectItemSlotIdentity(characterId, sourceSlot));
    }

    private static uint InspectItemSlotIdentity(int characterId, int sourceSlot)
    {
        var identity = unchecked(
            0x00064000u
            + ((uint)Math.Max(characterId, 0) * 32u)
            + (uint)Math.Max(sourceSlot, 0));
        return identity is 0 or uint.MaxValue ? 0x00064001u : identity;
    }

    private static uint InspectItemStateIdentity(int characterId, int sourceSlot, CompactItemEntry item)
    {
        var hash = 2166136261u;

        AddInspectIdentityValue(ref hash, unchecked((uint)characterId));
        AddInspectIdentityValue(ref hash, unchecked((uint)sourceSlot));
        AddInspectIdentityValue(ref hash, item.Id);
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute1));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute2));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute3));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute4));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute5));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel1));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel2));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel3));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel4));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel5));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Quality));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Grade));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Bound));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Stack));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Exp));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.HolySuitCode));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.SocketCount));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket1EffectId));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket1Level));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket2EffectId));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket2Level));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket3EffectId));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket3Level));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket4EffectId));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket4Level));

        return hash is 0 or uint.MaxValue ? 0x3E000001u : hash;
    }

    private static uint NullableInspectIdentityValue(int? value)
    {
        return value.HasValue ? unchecked((uint)value.Value) : uint.MaxValue;
    }

    private static uint NullableInspectIdentityValue(short? value)
    {
        return value.HasValue ? unchecked((uint)value.Value) : uint.MaxValue;
    }

    private static void AddInspectIdentityValue(ref uint hash, uint value)
    {
        // Fixed FNV-1a mixing is deterministic across processes and runtimes.
        for (var shift = 0; shift < sizeof(uint) * 8; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 16777619u;
        }
    }

    private static void WriteSnapshotItemRecord(Span<byte> record, CompactItemEntry item)
    {
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[..4], item.Id);
        WriteNullableInt32(record.Slice(4, 4), item.Attribute1);
        WriteNullableInt32(record.Slice(8, 4), item.Attribute2);
        WriteNullableInt32(record.Slice(12, 4), item.Attribute3);
        WriteNullableInt32(record.Slice(16, 4), item.Attribute4);
        WriteNullableInt32(record.Slice(20, 4), item.Attribute5);
        record[24] = ClampByte(item.Quality);
        record[25] = ClampByte(item.Grade);
        record[26] = ClampByte(item.Bound);
        record[27] = ClampByte(item.Stack);
        WriteItemExtension(record, item);
    }

    private static void WriteItemExtension(Span<byte> record, CompactItemEntry item)
    {
        if (record.Length < 52 || item.IsEmpty)
        {
            return;
        }

        BinaryPrimitives.WriteInt32LittleEndian(record.Slice(28, 4), item.Exp);

        // Captured item records pack holy suit and holy-stone socket count into one dword.
        BinaryPrimitives.WriteInt16LittleEndian(
            record.Slice(32, 2),
            (short)Math.Clamp(item.HolySuitCode, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(
            record.Slice(34, 2),
            Math.Clamp(item.SocketCount, (short)0, NativeClientHolyStoneSocketCount));

        WriteHolyStoneValueRows(record, item);
    }

    private static void WriteHolyStoneValueRows(Span<byte> record, CompactItemEntry item)
    {
        var socketCount = Math.Clamp(item.SocketCount, (short)0, NativeClientHolyStoneSocketCount);
        if (socketCount > 0)
        {
            WriteHolyStoneSlot(record, 0, item.Socket1EffectId, item.Socket1Level);
        }

        if (socketCount > 1)
        {
            WriteHolyStoneSlot(record, 1, item.Socket2EffectId, item.Socket2Level);
        }

        if (socketCount > 2)
        {
            WriteHolyStoneSlot(record, 2, item.Socket3EffectId, item.Socket3Level);
        }

        if (socketCount > 3)
        {
            WriteHolyStoneSlot(record, 3, item.Socket4EffectId, item.Socket4Level);
        }

    }

    private static void WriteHolyStoneSlot(Span<byte> record, int slot, short? effectId, short? level)
    {
        var effectOffset = 36 + (slot * 2);
        var valueOffset = 44 + (slot * 2);
        if (record.Length < Math.Max(effectOffset, valueOffset) + 2)
        {
            return;
        }

        BinaryPrimitives.WriteInt16LittleEndian(record.Slice(effectOffset, 2), HolyStoneEffectCode(effectId, level));
        BinaryPrimitives.WriteInt16LittleEndian(record.Slice(valueOffset, 2), HolyStoneValue(effectId, level));
    }

    private static short HolyStoneEffectCode(short? effectId, short? level)
    {
        if (!effectId.HasValue || !level.HasValue)
        {
            return 0;
        }

        // Captured item records store holy-stone display levels zero-based:
        // code 209 is rendered by the client as effect 2, level 10.
        var encodedLevel = Math.Clamp(level.Value, (short)1, (short)10) - 1;
        var code = (effectId.Value * 100) + encodedLevel;
        return (short)Math.Clamp(code, 0, short.MaxValue);
    }

    private static short HolyStoneValue(short? effectId, short? level)
    {
        if (!effectId.HasValue || !level.HasValue)
        {
            return 0;
        }

        var safeLevel = Math.Clamp(level.Value, (short)1, (short)10);
        var values = effectId.Value switch
        {
            // Captured working records: effect 2 L9=748, effect 2 L10=796..800.
            1 or 2 => HolyStonePercentHigh,

            // Percent-based offensive stones.
            3 or 4 => HolyStonePercentHigh,

            // Flat offensive stones.
            5 or 6 => HolyStoneFlatHigh,

            // Captured working records: effect 7 L10=596..598.
            7 => HolyStonePercentMedium,

            // Captured working records: effect 8 L9=937, effect 8 L10=991.
            8 => HolyStoneFlatCrit,

            // Captured working records: effect 9 L8=477..481, L9=506..515; effect 10 L8=463..471.
            9 or 10 or 13 or 15 or 17 or 19 => HolyStonePercentMedium,

            // Captured working records: effect 12 L8=303..311.
            11 or 12 or 14 or 16 or 18 or 20 => HolyStoneFlatLow,

            _ => HolyStonePercentMedium
        };

        return values[safeLevel - 1];
    }

    private static readonly short[] HolyStonePercentHigh =
        [110, 170, 240, 320, 410, 500, 650, 850, 1100, 1400];

    private static readonly short[] HolyStonePercentMedium =
        [80, 120, 170, 230, 300, 370, 500, 700, 950, 1200];

    private static readonly short[] HolyStoneFlatHigh =
        [120, 190, 280, 380, 500, 620, 850, 1200, 1650, 2200];

    private static readonly short[] HolyStoneFlatCrit =
        [150, 240, 340, 460, 590, 720, 950, 1300, 1800, 2400];

    private static readonly short[] HolyStoneFlatLow =
        [60, 90, 130, 170, 210, 250, 350, 500, 700, 950];

    private static void WriteWorldObjectAppearance(
        Span<byte> packet,
        uint objectId,
        string templateKey,
        float x,
        float z,
        float facing)
    {
        packet.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(packet[..2], WorldObjectAppearanceLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(2, 2), 0x2724);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(4, 4), MonsterAppearanceType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(20, 4), 237);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(24, 4), 237);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(28, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(32, 4), 0);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(36, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(40, 4), facing);
        PacketText.WriteFixedAscii(packet.Slice(WorldObjectTemplateOffset, WorldObjectTemplateLength), templateKey);
    }

    private static void WriteNpcWorldObjectAppearance(
        Span<byte> packet,
        NpcSpawnDefinition spawn)
    {
        packet.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(packet[..2], WorldObjectAppearanceLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(2, 2), 0x2724);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.Slice(4, 4),
            spawn.AppearanceType == 0 ? NpcSpawnDefinitionFactory.DefaultAppearanceType : spawn.AppearanceType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), spawn.ObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(24, 4), 1521);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(28, 4), spawn.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(32, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(36, 4), spawn.Z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.Slice(40, 4),
            float.IsFinite(spawn.Facing) ? spawn.Facing : NpcSpawnDefinitionFactory.DefaultFacing);
        PacketText.WriteFixedAscii(
            packet.Slice(WorldObjectTemplateOffset, WorldObjectTemplateLength),
            spawn.TemplateKey);
    }

    private static void WriteNullableInt32(Span<byte> destination, int? value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, value ?? -1);
    }

    private static byte ClampByte(short value)
    {
        return (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
    }

    private static byte ToWorldProfessionByte(byte profession)
    {
        // Working-server captures show the world-spawn class byte matches the DB class id.
        return profession;
    }

    private static byte ToClientProfessionByte(byte profession)
    {
        // UI/detail packets use the DB/client gameplay class id. World-spawn visuals use a different avatar order.
        return profession;
    }

    private static string EquipmentFor(GameCharacter character)
    {
        return string.IsNullOrWhiteSpace(character.Equipment)
            ? GameDefaults.DefaultEquipment(character.Profession)
            : character.Equipment;
    }

    private static ReadOnlySpan<byte> ValidPacketStreamPrefix(ReadOnlySpan<byte> stream)
    {
        var offset = 0;
        while (offset + 4 <= stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            if (length < 4 || offset + length > stream.Length)
            {
                break;
            }

            offset += length;
        }

        return stream[..offset];
    }

    private static byte[] PatchReferencePlayerPackets(ReadOnlySpan<byte> stream, GameCharacter character)
    {
        using var output = new MemoryStream(stream.Length);
        var offset = 0;
        while (offset + 4 <= stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            if (length < 4 || offset + length > stream.Length)
            {
                break;
            }

            var packet = stream.Slice(offset, length);
            if (Contains(packet, ReferencePlayerName))
            {
                var patched = PlayerDetail(character);
                output.Write(patched);
            }
            else
            {
                output.Write(packet);
            }

            offset += length;
        }

        return output.ToArray();
    }

    private static void PatchReferencePlayerPacket(byte[] packet, GameCharacter character, int nameOffset)
    {
        if (packet.Length < nameOffset + 32)
        {
            return;
        }

        PacketText.WriteFixedAscii(packet.AsSpan(nameOffset, 32), character.Name);

        var fieldBase = nameOffset + 32;
        if (packet.Length > fieldBase)
        {
            packet[fieldBase] = character.Gender;
        }

        if (packet.Length >= fieldBase + 20)
        {
            // PlayerDetail and PlayerStatusUpdate share the captured transform
            // layout. Do not leak the fixed template player's 165/-97 position
            // when publishing an object-specific remote status packet.
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 4, 4), character.PositionX);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 8, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 12, 4), character.PositionZ);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 16, 4), 1f);
        }

        if (packet.Length >= fieldBase + 56)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 52, 4), ToClientProfessionByte(character.Profession));
        }

        if (packet.Length >= fieldBase + 64)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 60, 4), character.Level);
        }

        if (packet.Length >= fieldBase + 72)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 64, 4), character.CurrentHp);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 68, 4), character.CurrentMp);
        }

        if (packet.Length >= fieldBase + 112)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 104, 4), character.MaxHp);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 108, 4), character.MaxMp);
        }

        if (packet.Length >= fieldBase + 152 && character.CalculatedStats is { } stats)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 112, 4), stats.PhysicalAttack);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 116, 4), stats.PhysicalDefense);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 120, 4), stats.PhysicalAttack);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 124, 4), stats.PhysicalDefense);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 128, 4), stats.MagicAttack);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 132, 4), stats.MagicDefense);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 136, 4), stats.Hit);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 140, 4), stats.Dodge);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 144, 4), stats.Critical);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 148, 4), stats.CriticalResistance);
        }

        if (packet.Length >= fieldBase + 160 && character.CalculatedStats is { } extendedStats)
        {
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 152, 4), ToClientPercent(extendedStats.PhysicalDamageBonus));
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 156, 4), ToClientPercent(extendedStats.MagicDamageBonus));
        }

        if (packet.Length >= fieldBase + 164 && character.CalculatedStats is { } defensePierceStats)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 160, 4), defensePierceStats.DamageAbsorb);
        }

        if (packet.Length >= fieldBase + 172 && character.CalculatedStats is { } pierceStats)
        {
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 164, 4), ToClientPercent(pierceStats.IgnorePhysicalDefense));
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 168, 4), ToClientPercent(pierceStats.IgnoreMagicDefense));
        }
    }

    private static float ToClientPercent(int scaledPercent)
    {
        return scaledPercent / 10000f;
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static void ReplaceAscii(Span<byte> packet, ReadOnlySpan<byte> search, ReadOnlySpan<byte> replacement)
    {
        if (search.Length == 0 || search.Length != replacement.Length)
        {
            return;
        }

        for (var i = 0; i <= packet.Length - search.Length; i++)
        {
            if (!packet.Slice(i, search.Length).SequenceEqual(search))
            {
                continue;
            }

            replacement.CopyTo(packet.Slice(i, replacement.Length));
            i += search.Length - 1;
        }
    }
}
