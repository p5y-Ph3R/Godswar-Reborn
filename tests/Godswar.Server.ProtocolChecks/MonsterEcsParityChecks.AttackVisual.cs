using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterEcsParityChecks
{
    private static void CheckMonsterAttackCastVisualLayout()
    {
        var packet = PacketBuilder.MonsterSkillCastVisual(
            casterObjectId: 0x1BC,
            targetObjectId: 0x2745,
            skillId: 500,
            casterX: BitConverter.Int32BitsToSingle(0x433A4007),
            casterZ: BitConverter.Int32BitsToSingle(0x41DFB82A),
            targetX: BitConverter.Int32BitsToSingle(0x433DFE20),
            targetZ: BitConverter.Int32BitsToSingle(0x41F74BD9));
        var captured = Convert.FromHexString(
            "28003827BC010000F401000000000000452700000A000000" +
            "07403A432AB8DF4120FE3D43D94BF741");

        Check.True(
            packet.SequenceEqual(captured),
            "monster cast visual preserves native caster and target coordinates");
    }
}
