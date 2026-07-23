using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] ForgeResult(bool success, int resultKind)
    {
        const int packetLength = 40;
        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.ForgeStart);
        // Client object offsets include a four-byte transport prefix that is
        // absent from the wire. Its object +8 success field is packet +4, and
        // object +12 result kind is packet +8.
        packet[4] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), resultKind);
        return packet;
    }

    public static byte[] ZodiacFullSync(GameCharacter character)
    {
        return ZodiacFullSync(character, DateTimeOffset.UtcNow);
    }

    public static byte[] ZodiacFullSync(GameCharacter character, DateTimeOffset now)
    {
        const int headerLength = 24;
        const int stateLength = 304;
        const ushort fullSyncSid = 1;
        var packet = new byte[headerLength + stateLength];
        var experienceX100 = Math.Max(0, character.ZodiacAccumulatedExperienceX100);
        var talentExperienceX100 = Math.Max(0, character.ZodiacAccumulatedTalentExperienceX100);

        WriteZodiacHeader(
            packet,
            LocalPlayerObjectId,
            fullSyncSid,
            experienceX100,
            talentExperienceX100,
            value3: 1);

        var state = packet.AsSpan(headerLength, stateLength);
        var zodiacType = character.ZodiacType <= 11 ? character.ZodiacType : (byte)0;
        var luckyStatus = character.ZodiacLuckyStatus > 0 &&
            (character.ZodiacLuckyExpiresAt is null || character.ZodiacLuckyExpiresAt > now)
                ? 1
                : 0;
        var zodiacLevel = Math.Clamp((int)character.ZodiacLevel, 1, 30);

        BinaryPrimitives.WriteInt32LittleEndian(state.Slice(0, 4), zodiacType);
        BinaryPrimitives.WriteInt32LittleEndian(state.Slice(4, 4), luckyStatus);
        state[8] = checked((byte)zodiacLevel);
        BinaryPrimitives.WriteInt32LittleEndian(
            state.Slice(12, 4),
            Math.Max(0, character.ZodiacEnergy));

        // The client copies the whole state first and then replaces these two
        // floats with header v1/v2. Keeping both representations aligned makes
        // the packet safe for clients that read either location directly.
        BinaryPrimitives.WriteSingleLittleEndian(state.Slice(40, 4), experienceX100);
        BinaryPrimitives.WriteSingleLittleEndian(state.Slice(44, 4), talentExperienceX100);

        // Native Origin.exe constructs two zero-filled 12-byte stone records
        // at +16/+28. The sixteen 16-byte training-grid records start at +48
        // and fill the remainder of the 304-byte state exactly.
        for (var gridIndex = 0;
             gridIndex < ZodiacSkillGridCatalog.GridCount;
             gridIndex++)
        {
            var grid = state.Slice(48 + (gridIndex * 16), 16);
            BinaryPrimitives.WriteInt32LittleEndian(
                grid.Slice(0, 4),
                ZodiacSkillGridCatalog.PackClientLevel(
                    gridIndex,
                    ZodiacSkillGridCatalog.GetLevel(character, gridIndex)));
            BinaryPrimitives.WriteInt32LittleEndian(
                grid.Slice(4, 4),
                ZodiacSkillGridCatalog.GetSelectedSkillId(
                    character,
                    gridIndex));
        }

        return packet;
    }

    public static byte[] ZodiacLevelUpgrade(byte currentLevel, int currentEnergy)
    {
        var packet = new byte[24];
        WriteZodiacHeader(
            packet,
            LocalPlayerObjectId,
            sid: 3,
            Math.Clamp((int)currentLevel, 1, 30),
            Math.Max(0, currentEnergy),
            value3: 0);
        return packet;
    }

    public static byte[] ZodiacSkillGridActivated(int gridIndex)
    {
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }

        var packet = new byte[24];
        WriteZodiacHeader(
            packet,
            LocalPlayerObjectId,
            sid: 100,
            gridIndex,
            value2: 0,
            value3: 0);
        return packet;
    }

    public static byte[] ZodiacSkillGridUpgraded(int gridIndex)
    {
        if (!ZodiacSkillGridCatalog.IsValidGrid(gridIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }

        var packet = new byte[24];
        WriteZodiacHeader(
            packet,
            LocalPlayerObjectId,
            sid: 101,
            gridIndex,
            value2: 0,
            value3: 0);
        return packet;
    }

    public static byte[] ZodiacAccumulationGain(
        GameCharacter character,
        int experience,
        int talentExperience)
    {
        var packet = new byte[24];
        WriteZodiacHeader(
            packet,
            LocalPlayerObjectId,
            sid: 7,
            Math.Max(0, experience),
            Math.Max(0, talentExperience),
            value3: 0);
        return packet;
    }

    public static byte[] ZodiacEnergyIncrease(int currentEnergy, int gainedEnergyX100)
    {
        var packet = new byte[24];
        WriteZodiacHeader(
            packet,
            LocalPlayerObjectId,
            sid: 5,
            Math.Max(0, currentEnergy),
            Math.Max(0, gainedEnergyX100),
            value3: 0);
        return packet;
    }

    private static void WriteZodiacHeader(
        Span<byte> packet,
        uint playerId,
        ushort sid,
        int value1,
        int value2,
        int value3)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(0, 2), checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(2, 2), Opcodes.Zodiac);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(4, 4), playerId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(10, 2), sid);
        BinaryPrimitives.WriteInt32LittleEndian(packet.Slice(12, 4), value1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.Slice(16, 4), value2);
        BinaryPrimitives.WriteInt32LittleEndian(packet.Slice(20, 4), value3);
    }
}
