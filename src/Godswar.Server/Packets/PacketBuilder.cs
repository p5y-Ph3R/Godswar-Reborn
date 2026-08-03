using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int OriginalServerUtcOffsetSeconds = -8 * 60 * 60;
    private const int EnterPlayerDatabaseIdOffset = 4;
    private const int CharacterNameOffsetInEnterTemplate = 8;
    private const int EnterTalentExperienceOffset = 96;
    private const int EnterTalentPointsOffset = 92;
    private const int EnterPlayerObjectIdOffset = 52;
    private const int EnterPositionXOffset = 56;
    private const int EnterPositionYOffset = 60;
    private const int EnterPositionZOffset = 64;
    private const int EnterMaxHpOffset = 68;
    private const int EnterMaxMpOffset = 72;
    private const int EnterCurrentHpOffset = 76;
    private const int EnterCurrentMpOffset = 80;
    private const int EnterExperienceOffset = 84;
    private const int EnterNextLevelExperienceOffset = 88;
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
    private const ushort AfterLoginOpcode = 0x2876;
    private const ushort PlayerInspectEquipmentOpcode = 0x2726;
    private const ushort PlayerInspectProfileOpcode = 0x2772;
    private const ushort PlayerInspectCompleteOpcode = 0x2826;
    private const int PlayerInspectProfileLength = 336;
    private const short CapturedWorldVisualQualityCap = 13;
    private const short CapturedWorldVisualGradeCap = 12;
    private const ushort EnterMainOpcode = 0x2723;
    private const ushort KitBagDetailOpcode = 0x2731;
    private const ushort BagItemActionOpcode = 0x2748;
    private const ushort NpcDialogOpenOpcode = 0x2753;
    private const ushort NpcFunctionActionResponseOpcode = 0x2756;
    private const ushort EnterCompleteOpcode = 0x2715;
    private const ushort MonsterMovementStartOpcode = 0x2720;
    private const ushort MonsterMovementEndOpcode = 0x2721;
    private const ushort MonsterLifecycleMarkerOpcode = 0x2727;
    private const ushort PlayerWorldSpawnOpcode = 0x2725;
    private const ushort WorldObjectRemoveOpcode = 0x2728;
    private const ushort PlayerDetailOpcode = 0x273B;
    private const int PlayerDetailMaxHpOffset = 100;
    private const int PlayerDetailMaxMpOffset = 104;
    private const int PlayerDetailCurrentHpOffset = 108;
    private const int PlayerDetailCurrentMpOffset = 112;
    private const int PlayerDetailSilverOffset = 116;
    private const int PlayerDetailGoldOffset = 120;
    private const ushort TalentRankListOpcode = 0x273A;
    private const ushort TalentSkillUnlockListOpcode = 0x2739;
    private const ushort SkillListOpcode = 0x27D4;
    private const ushort SkillDamageOpcode = 0x273D;
    private const ushort SkillCastImpactOpcode = 0x273E;
    private const ushort SkillClusterDamageOpcode = 0x273F;
    private const ushort PhysicalDamageOpcode = 0x272A;
    private const ushort MonsterDeathRewardOpcode = 0x272B;
    private const ushort PlayerDeathOpcode = 0x2722;
    private const ushort PlayerLevelUpOpcode = 0x272E;
    private const ushort ExperienceGainOpcode = 0x272F;
    private const ushort AttributeGainOpcode = 0x2845;
    private const ushort PlayerManaUpdateOpcode = 0x2797;
    private const ushort PlayerVitalsUpdateOpcode = 0x2771;
    private const ushort PlayerUnknown10098Opcode = 0x2772;
    private const ushort PlayerDetailAckOpcode = 0x27DA;
    private const int PlayerWorldVisualFlagsOffset = 81;
    private const int PlayerWorldVisualFlagsLength = 18;
    private const int PlayerWorldAttributeCountsOffset = 102;
    private const int PlayerWorldAttributeCountsLength = 17;
    private const int PlayerWorldEquipmentIdsOffset = 124;
    private const int PlayerWorldEquipmentIdsLength = 18;
    private const int PlayerWorldEquipmentMaskOffset = 168;
    private const int PlayerWorldStatusCountOffset = 178;
    private const int PlayerWorldStatusIdsOffset = 180;
    private const int PlayerWorldStatusMaximumCount = 20;
    private const int PlayerWorldNativeLength = 260;
    private const int PlayerWorldFullVisualMarkerOffset = PlayerWorldNativeLength;
    private const int PlayerWorldFullVisualQualityOffset = PlayerWorldFullVisualMarkerOffset + sizeof(uint);
    private const int PlayerWorldFullVisualGradeOffset =
        PlayerWorldFullVisualQualityOffset + PlayerWorldEquipmentIdsLength;
    private const int PlayerWorldExtendedLength =
        PlayerWorldFullVisualGradeOffset + PlayerWorldEquipmentIdsLength;
    // ASCII "GWX1" on the wire. Patched clients require both this marker and
    // the extended packet length before reading the appended full-byte fields.
    private const uint PlayerWorldFullVisualMarker = 0x31585747;
    private const short NativeClientHolyStoneSocketCount = 4;
    // ASCII "GWA3" in little-endian order. The native 72-byte stride carries
    // one optional class ID plus two packed elemental IDs.
    private const uint ClassSuitAttributeExtensionMarker = 0x33415747;
    private const uint LocalPlayerObjectId = 0x00001448;
    private const int WorldObjectAppearanceLength = 108;
    private const int WorldObjectTemplateOffset = 44;
    private const int WorldObjectTemplateLength = WorldObjectAppearanceLength - WorldObjectTemplateOffset;
    private static readonly byte[] AthensTemplatePrefix = [(byte)'A', (byte)'t', (byte)'h', (byte)'e', (byte)'n', (byte)'s', (byte)'_'];
    private static readonly byte[] SpartaTemplatePrefix = [(byte)'S', (byte)'p', (byte)'a', (byte)'r', (byte)'t', (byte)'a', (byte)'_'];
    private static readonly byte[] ReferencePlayerName = [(byte)'s', (byte)'u', (byte)'s', (byte)'h', (byte)'1'];
    // This manifest is byte-for-byte stable across nine working-original login
    // captures. The trailing "88" is part of every 44-byte record; omitting it,
    // record 69, or the repeated final record leaves the legacy client bootstrap
    // incomplete and can race character-preview resource initialization.
    private static readonly (int Id, string Hash)[] AfterLoginManifest =
    [
        (0, "246ac788338515372d951d4eabe0e252"),
        (1, "246ac788338515372d951d4eabe0e252"),
        (2, "a5edc85cff0c55bc297eef2c19dcb3bf"),
        (3, "8bdf99407ef38cb94b2c93aa45eedae1"),
        (4, "a5edc85cff0c55bc297eef2c19dcb3bf"),
        (5, "5b829bd9c1da8a306b6c2ae989806fa8"),
        (6, "cf9d92b17936ba6218c0734d094e77fd"),
        (7, "70ae92d57ef3b9544729ba53fd52a3fb"),
        (8, "6a8117051b7c213667c805d1f6340345"),
        (9, "04105589fa800caaed7a6e41c6b05597"),
        (10, "938ba89425a4c1ff514cef8a35ecaa6c"),
        (11, "140a860899e858352ee9b7a3daa54725"),
        (12, "36da28bce4861a5d778653d34b2ff9eb"),
        (13, "9d345aaac44ca4e2d6b4f4af83633ab9"),
        (14, "c272ff740b5d41d974bb4d498284239e"),
        (15, "3e0201d94dc2f7d658ad6f37bb0ae53b"),
        (16, "1cd714aa1aac559e2bc5471bc879294e"),
        (17, "6a8f244543c22447be78fcfa1afe836b"),
        (18, "198d10dcbaa73e15f756d73b9e76527a"),
        (19, "9330df8bfe7a0ddba11a1a89583077c5"),
        (20, "2fa8490b013f6bfea9a5cfc3e15cdf43"),
        (21, "e52c576f0fca41b8950ecdae0504ac2a"),
        (22, "44befdd3bb5e2f3dc0d93ebb4f8865f8"),
        (23, "74f8fe256549638920f635404228e17b"),
        (24, "3ded766739589dbdf9dd329e26dac9c7"),
        (25, "49b83e2e9cc3d8e27b328f705c8ebfcd"),
        (26, "eb6561d0ab648f79dd6b917515d90c04"),
        (27, "fe5d48cfcbc86314add7229635f9f6af"),
        (28, "3ded766739589dbdf9dd329e26dac9c7"),
        (29, "2c6ef021a5a0600b4e241aaaafe8ff26"),
        (30, "370382b5021b778dd879cb1df900cb25"),
        (31, "370382b5021b778dd879cb1df900cb25"),
        (32, "57ee44bb3ac0cae48ff30833904c2067"),
        (33, "b8ef14a908ff9716df53fb8e54e0a55a"),
        (34, "fe5d48cfcbc86314add7229635f9f6af"),
        (35, "307d1665f359d20932245433ccad58fa"),
        (36, "307d1665f359d20932245433ccad58fa"),
        (37, "307d1665f359d20932245433ccad58fa"),
        (38, "431a9578b218ef804c03d7e7a6eb0d90"),
        (39, "d004300f7c9c97f7ffd23b4dfd4b205c"),
        (40, "5374ee745ea6f10e50aee2da29e75b50"),
        (41, "cb39f985fa69a9aacadbdd399c251805"),
        (42, "b599e41d1950710f6e06675b3a9b6540"),
        (43, "0e2c21e07f4c89c03a408819821cad8f"),
        (44, "4822aa76a4ec1aba9315f5f133078f53"),
        (45, "09d7a12b378edd30bbd2c2e6d029bfc9"),
        (46, "4365451ac5933c2a947a2756a11e6554"),
        (56, "cb39f985fa69a9aacadbdd399c251805"),
        (57, "ef4c7faa4a8ad773e5496321cd9408e0"),
        (68, "95aec19ac717133cf3c4a47bb52025a5"),
        (69, "2dff1b2d27dd1975eeedf5e031d9fc81"),
        (200, "c5ed09ab822f6d54f9909783485358c4"),
        (201, "19882a36ee8a2aa29e92c0d4c27f5c37"),
        (202, "19882a36ee8a2aa29e92c0d4c27f5c37"),
        (203, "19882a36ee8a2aa29e92c0d4c27f5c37"),
        (204, "c5ed09ab822f6d54f9909783485358c4"),
        (205, "fbbed743004757397681e2cad81b10dd"),
        (206, "19882a36ee8a2aa29e92c0d4c27f5c37"),
        (207, "7a885c86f58fd3aa1ab976a5554fce3c"),
        (208, "ee391fac52295f20f89e096bda2c1cd7"),
        (209, "ee391fac52295f20f89e096bda2c1cd7"),
        (210, "6e0b41b9c05479d3e1d21b9c4f438167"),
        (210, "6e0b41b9c05479d3e1d21b9c4f438167")
    ];
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
    private static readonly byte[] CapturedSevenPetListTemplate = Convert.FromHexString(
        "a004fd2708070000030000000000000000000000000000000000000000000000000000000000000000000000280701017800000c0c646401020c1c15500c081024138b13ef1333119c187f15780eb814e415b004b004000001000000010000000100000001000000a0b2a5680000000019000000438e0000baac0000820c0000e009000020090000f58400009d2e0100da24010087220100b626010075230100951401003f0d030f0f06e200010000001bd1010000000000000000000000000000000000000000000000000000000000000000000a04020078000001016464010001b50400000000000000000000000000000000000000000000f401f401000001000000010000000100000001000000c9edb20200000000070000007d000000a5000000a70000008a000000e0000000f10000009405000013200000f32e000074140000371e0000570f0000d200050000000000000000001cd1010000000000000000000000000000000000000000000000000000000000000000000a04020078000001016464010001b50400000000000000000000000000000000000000000000f401f4010000010000000100000001000000010000006df87a0100000000070000000a00000097000000930000007400000047000000ff00000095260000e41b000030250000ae1e0000ce0f00008a200000ba00010000000000000000001dd1010000000000000000000000000000000000000000000000000000000000000000000a04020178000001016464010001b50400000000000000000000000000000000000000000000f401f4010000010000000100000001000000010000003cfeb401000000000700000082000000000100002c010000010000001d00000005010000e41b0000331100005e0800009c1f0000f32e0000160c0000aa00020100000000000000001ed1010000000000000000000000000000000000000000000000000000000000000000000a04020178000001026464010001b50400000000000000000000000000000000000000000000f401f401000001000000010000000100000001000000c331e10000000000070000008a0000001600000045000000160100000400000025000000690e0000081a000066220000d21c0000463100001a190000be00010100000000000000001fd1010000000000000000000000000000000000000000000000000000000000000000000a04020078000001036464010001b50400000000000000000000000000000000000000000000f401f401000001000000010000000100000001000000dcdd3d03000000000700000079010000cd0000001b000000cb010000b90100007e000000280b000030250000f9060000371e0000052e000054230000ec000600000000000000000020d1010000000000000000000000000000000000000000000000000000000000000000000a0402006b000001036464010001b50400000000000000000000000000000000000000000000f401f40100000100000001000000010000000100000083fe17003c714300070000000c000000ef0000001802000078000000e9000000200100009c23000068290000161a00006e18000082260000ec2600000e0103010000000000000000");
    private static readonly byte[] CapturedFourPetListTemplate = Convert.FromHexString(
        "A802FD270404000039120000000000000000000000000000000000000000000000000000000000000000000027070201780000050564640102043011EF131C157F1500000000000000000000000000000000B004B00400000100000001000000010000000100000035D02B83000000001900000071050000BA0400001F060000AB0600004F0500009C07000006C6000018C50000B3C30000A1C4000047C900003CC30000B1040A0303000200010000003F1D000000000000000000000000000000000000000000000000000000000000000000000407030001000001036464010001D50700000000000000000000000000000000000000000000B004B0040000010000000100000001000000010000007F665108DC05000019000000DD0400002303000086020000F2020000A9030000AE030000000000000000000000000000000000000000000000000000E2010B0000000000010000003E1601000000000000000000000000000000000000000000000000000000000000000000020201011500000101646401000125030000000000000000000000000000000000000000000090019001000001000000010000000100000001000000FA68050031C60800000000006F00000061000000B50000000B01000049000000B7000000E8030000F4010000D002000078000000FC0300005C0300008B0001000000000001000000D1610100000000000000000000000000000000000000000000000000000000000000000001020301010000010364640100019501000000000000000000000000000000000000000000005802580200000100000001000000010000000100000000000000DC05000007000000320000006E00000061000000260000007B00000062000000000000000000000000000000000000000000000000000000540000000000000000000000");
    private static readonly byte[] SkillListBootstrapTemplate = Convert.FromHexString(
        "E400D4271605000012000000C40900000101000000000000140500000001000000000000150500000001000000000000160500000001000000000000BC0B00000201000000000000BE0B00000201000000000000C20B00000201000000000000C30B00000201000000000000C40B00000201000000000000C60B00000201000000000000C70B00000201000000000000B80B00000201000000000000B90B00000201000000000000BD0B00000201000000000000FE1300000401000000000000961300000401000000000000FA1300000401000000000000FB1300000401000000000000");
    private static readonly byte[] CapturedEmptyPetListTemplate = Convert.FromHexString("0800FD2702000000");
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
        EquipmentSlots.Stylish,
        EquipmentSlots.MountHead,
        EquipmentSlots.MountArmor,
        EquipmentSlots.MountSoul,
        EquipmentSlots.MountOrnament,
        EquipmentSlots.MountAmulet,
        EquipmentSlots.Mount
    ];
    // Inspection considers source slots 0..20 in ascending order. Non-empty items
    // are packed into the record array and this source ordering is reconstructed by
    // the trailing slot mask (including cosmetic/title slots 15..20).
    private static readonly int[] InspectEquipmentSlots =
        Enumerable.Range(0, PlayerInspectEquipmentRecordCount).ToArray();

}
