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

        return Task.CompletedTask;
    }
}
