using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class SkillCastInterruptPacketChecks
{
    public static Task RunAsync()
    {
        const uint localObjectId = 0x1448;
        var expected = Convert.FromHexString("0800BB2748140000");

        Check.True(
            PacketBuilder.SkillCastInterrupt(localObjectId).SequenceEqual(expected),
            "cast interruption packet matches the native eight-byte frame");
        Check.Equal(
            "SkillCastInterrupt",
            Opcodes.Name(Opcodes.SkillCastInterrupt),
            "cast interruption opcode uses its bidirectional protocol name");
        Check.True(
            PacketBuilder.LocalizedError(
                    NativeErrorCodes.InsufficientMana)
                .SequenceEqual(Convert.FromHexString(
                    "0C00E827000000006C000000")),
            "insufficient MP uses native ERROR_006C left-log notice");
        Check.True(
            PacketBuilder.LocalizedError(
                    NativeErrorCodes.SkillNotReady)
                .SequenceEqual(Convert.FromHexString(
                    "0C00E827000000006D000000")),
            "cooldown rejection uses native ERROR_006D left-log notice");

        return Task.CompletedTask;
    }
}
