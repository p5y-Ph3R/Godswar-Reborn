using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class LegacyFighterExperienceWireChecks
{
    public const string CheckName =
        "Legacy-client unsigned fighter EXP wire boundaries";

    private static readonly (long Value, string LittleEndianHex)[] Boundaries =
    [
        (2_147_483_647L, "FFFFFF7F"),
        (2_147_483_648L, "00000080"),
        (4_000_000_000L, "00286BEE"),
        (4_294_967_295L, "FFFFFFFF")
    ];

    public static Task RunAsync()
    {
        Check.Equal(
            4_294_967_295L,
            PacketBuilder.MaximumLegacyFighterExperience,
            "legacy fighter EXP ceiling");

        var normalLevel89 = CreateCharacter(0);
        Check.Equal(
            (uint)PlayerExperienceCatalog.GetNextLevelExperience(89),
            ReadUnsignedField(
                PacketBuilder.EnterMain(normalLevel89),
                offset: 88),
            "unsealed level-89 EXP bar uses the next-level threshold");

        var sealedLevel89 = CreateCharacter(0);
        sealedLevel89.FighterLevelSealed = true;
        Check.Equal(
            uint.MaxValue,
            ReadUnsignedField(
                PacketBuilder.EnterMain(sealedLevel89),
                offset: 88),
            "sealed level-89 EXP bar uses the UInt32 storage ceiling");

        var normalLevel199 = CreateCharacter(0);
        normalLevel199.Level = 199;
        Check.Equal(
            (uint)PlayerExperienceCatalog.GetNextLevelExperience(199),
            ReadUnsignedField(
                PacketBuilder.EnterMain(normalLevel199),
                offset: 88),
            "level-199 EXP bar uses its normal next-level threshold");

        var cappedLevel200 = CreateCharacter(0);
        cappedLevel200.Level = PlayerExperienceCatalog.MaximumLevel;
        Check.Equal(
            uint.MaxValue,
            ReadUnsignedField(
                PacketBuilder.EnterMain(cappedLevel200),
                offset: 88),
            "level-200 EXP bar uses the UInt32 storage ceiling");

        foreach (var (value, expectedHex) in Boundaries)
        {
            var character = CreateCharacter(value);
            CheckField(
                PacketBuilder.EnterMain(character),
                offset: 84,
                expectedHex,
                $"world-entry total {value}");
            CheckField(
                PacketBuilder.PlayerDetail(character),
                offset: 92,
                expectedHex,
                $"player-detail total {value}");
            CheckField(
                PacketBuilder.PlayerStatusUpdate(character, objectId: 3),
                offset: 96,
                expectedHex,
                $"player-status total {value}");
            CheckField(
                PacketBuilder.ExperienceGain(value, value),
                offset: 4,
                expectedHex,
                $"EXP-gain delta {value}");
            CheckField(
                PacketBuilder.ExperienceGain(1, value),
                offset: 8,
                expectedHex,
                $"EXP-gain total {value}");
            CheckField(
                PacketBuilder.MonsterDeathReward(1, 2, value, 0, 0),
                offset: 48,
                expectedHex,
                $"monster reward total {value}");
            CheckField(
                PacketBuilder.PlayerLevelUp(2, 89, 1, value, 1, 1, 1, 1),
                offset: 16,
                expectedHex,
                $"level-up total {value}");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.ExperienceGain(-1, 0),
            "negative gained fighter EXP is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.ExperienceGain(0, 4_294_967_296L),
            "fighter EXP above UInt32 is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.MonsterDeathReward(1, 2, -1, 0, 0),
            "negative monster-reward fighter EXP is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerLevelUp(2, 89, 1, 4_294_967_296L, 1, 1, 1, 1),
            "level-up fighter EXP above UInt32 is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.EnterMain(CreateCharacter(-1)),
            "negative world-entry fighter EXP is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusUpdate(
                CreateCharacter(4_294_967_296L),
                objectId: 3),
            "player-status fighter EXP above UInt32 is rejected");

        var invalidSeal = CreateCharacter(0);
        invalidSeal.Level = 88;
        invalidSeal.FighterLevelSealed = true;
        Check.Throws<InvalidOperationException>(
            () => PacketBuilder.EnterMain(invalidSeal),
            "fighter EXP bar rejects a level seal outside level 89");

        return Task.CompletedTask;
    }

    private static uint ReadUnsignedField(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(offset, sizeof(uint)));

    private static void CheckField(
        byte[] packet,
        int offset,
        string expectedHex,
        string description)
    {
        var actualHex = Convert.ToHexString(packet.AsSpan(offset, sizeof(uint)));
        Check.Equal(expectedHex, actualHex, description);
    }

    private static GameCharacter CreateCharacter(long experience) => new()
    {
        Id = 1,
        AccountId = 1,
        Name = "uint-exp-wire",
        Level = 89,
        Experience = experience,
        CurrentHp = 1,
        MaxHp = 1,
        MaxMp = 1,
        CurrentMp = 1
    };
}
