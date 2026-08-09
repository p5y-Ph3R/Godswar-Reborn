using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerRuntimeEcsCutoverChecks
{
    private static void AssertAppliedRuntimeGameData(
        byte[] packet,
        GameCharacter character,
        SkillStatusEffectDefinition definition)
    {
        Check.Equal(
            character.CalculatedStats!.Hit + definition.HitBonus,
            ReadInt32(packet, 176),
            "runtime Hit modifier refreshes local GameData");
        Check.Equal(
            character.CalculatedStats.Critical +
                definition.CriticalAppendBonus,
            ReadInt32(packet, 184),
            "runtime Critical modifier refreshes local GameData");
    }

    private static void AssertExpiredRuntimeGameData(
        byte[] packet,
        GameCharacter character)
    {
        Check.Equal(
            character.CalculatedStats!.Hit,
            ReadInt32(packet, 176),
            "runtime expiry restores base Hit in local GameData");
        Check.Equal(
            character.CalculatedStats.Critical,
            ReadInt32(packet, 184),
            "runtime expiry restores base Critical in local GameData");
    }
}
