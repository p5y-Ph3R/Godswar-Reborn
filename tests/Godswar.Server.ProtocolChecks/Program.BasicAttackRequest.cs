using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static void CheckBasicAttackRequestFraming(
        byte[] captured,
        in BasicAttackRequest parsed)
    {
        var alternateTail = captured.ToArray();
        alternateTail.AsSpan(24, 8).Fill(0xA5);
        Check.True(
            BasicAttackRequest.TryParse(
                alternateTail,
                out var alternateTailParsed) &&
            alternateTailParsed == parsed,
            "basic attack tail bytes are framed but excluded from authority");

        var trailing = new byte[captured.Length + 1];
        captured.CopyTo(trailing, 0);
        Check.True(
            !BasicAttackRequest.TryParse(trailing, out _),
            "basic attack rejects trailing bytes outside the native frame");

        var wrongOpcode = captured.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongOpcode.AsSpan(2, 2),
            Opcodes.SkillCast);
        Check.True(
            !BasicAttackRequest.TryParse(wrongOpcode, out _),
            "basic attack rejects a wrong opcode in an exact-size frame");

        var wrongLength = captured.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongLength.AsSpan(0, 2),
            31);
        Check.True(
            !BasicAttackRequest.TryParse(wrongLength, out _) &&
            !BasicAttackRequest.TryParse(captured.AsSpan(0, 31), out _),
            "basic attack rejects malformed declared and physical lengths");
    }
}
