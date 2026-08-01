using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class SkillCombatTimingCatalogChecks
{
    public static Task RunAsync()
    {
        CheckTiming(0, TimeSpan.Zero, TimeSpan.FromSeconds(10), "Light Chop");
        CheckTiming(530, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(22), "Thunder");
        CheckTiming(570, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2), "Flame Blast");
        CheckTiming(580, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(180), "Fire Blast");
        CheckTiming(4904, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6), "Riding");
        return Task.CompletedTask;
    }

    private static void CheckTiming(
        int skillId,
        TimeSpan expectedCastTime,
        TimeSpan expectedCooldown,
        string label)
    {
        Check.True(
            GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                skillId,
                out var definition),
            $"{label} combat definition exists");
        Check.Equal(expectedCastTime, definition.CastTime, $"{label} cast time");
        Check.Equal(expectedCooldown, definition.Cooldown, $"{label} cooldown");
    }
}
