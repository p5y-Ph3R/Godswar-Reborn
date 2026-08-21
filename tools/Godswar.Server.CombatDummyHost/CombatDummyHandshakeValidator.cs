using System.Buffers.Binary;
using System.Text;

namespace Godswar.Server.CombatDummyHost;

internal static class CombatDummyHandshakeValidator
{
    public const ushort EnterMainOpcode = 0x2723;
    public const ushort NpcSpawnOpcode = 10020;

    public static void ValidateCharacterPreview(
        CombatDummyDefinition definition,
        ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 44 ||
            packet[4] != 1 ||
            !string.Equals(
                ReadFixedAscii(packet.Slice(5, 32)),
                definition.CharacterName,
                StringComparison.Ordinal) ||
            packet[37] != definition.Camp ||
            packet[38] != definition.Profession ||
            packet[39] != 160)
        {
            throw new InvalidDataException(
                "CharacterPreview did not match the immutable level-160 " +
                "dummy identity.");
        }
    }

    public static void ValidateEnterMain(
        CombatDummyDefinition definition,
        ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 104 ||
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(4, 4)) !=
                definition.CharacterId ||
            !string.Equals(
                ReadFixedAscii(packet.Slice(8, 32)),
                definition.CharacterName,
                StringComparison.Ordinal) ||
            packet[41] != definition.Camp ||
            packet[43] != definition.Profession ||
            packet[46] != definition.MapId ||
            !Approximately(
                BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(56, 4)),
                definition.PositionX) ||
            !Approximately(
                BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(64, 4)),
                definition.PositionZ))
        {
            throw new InvalidDataException(
                "EnterMain did not match the immutable dummy identity, " +
                "capital, or configured crier-adjacent position.");
        }

        var maximumHp = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Slice(68, 4));
        var maximumMp = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Slice(72, 4));
        var currentHp = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Slice(76, 4));
        var currentMp = BinaryPrimitives.ReadInt32LittleEndian(
            packet.Slice(80, 4));
        if (maximumHp <= 0 || currentHp != maximumHp ||
            maximumMp <= 0 || currentMp != maximumMp)
        {
            throw new InvalidDataException(
                "Training dummy did not enter with full authoritative " +
                "health and mana.");
        }
    }

    public static bool ObserveWorldReady(
        ref bool observedNpc,
        ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4)
        {
            return false;
        }

        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(
            packet.Slice(2, 2));
        observedNpc |= opcode == NpcSpawnOpcode;
        return observedNpc &&
            opcode == 10167 &&
            packet.Length == 340 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4)) ==
                DummyPackets.LocalPlayerObjectId;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(
            terminator < 0 ? value : value[..terminator]);
    }

    private static bool Approximately(float left, float right) =>
        float.IsFinite(left) && MathF.Abs(left - right) <= 0.01f;
}
