using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class MonsterRewardCatalog
{
    private const int CapturedNormalMonsterMultiplier = 4;
    internal const int NormalTalentExperience = 2;

    // Original server MonsterEXP table. Captured normal Sparta monsters use a
    // template multiplier of four: tier 1 therefore awards 20 * 4 = 80 EXP.
    private static readonly int[] MonsterExperience =
    [
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 32, 33, 34, 35, 36, 37, 38, 39, 40,
        41, 42, 43, 44, 45, 47, 48, 49, 50, 51,
        52, 53, 54, 56, 57, 58, 59, 60, 61, 62,
        64, 65, 66, 67, 68, 69, 71, 72, 73, 74,
        75, 77, 78, 79, 80, 81, 83, 84, 85, 86,
        88, 89, 90, 91, 93, 94, 95, 96, 98, 99,
        100, 101, 103, 104, 105, 106, 108, 109, 110, 111,
        113, 114, 115, 117, 118, 119, 120, 122, 123, 124,
        126, 127, 128, 130, 131, 132, 134, 135, 136, 137,
        139, 140, 141, 143, 144, 145, 147, 148, 149, 151,
        152, 154, 155, 156, 158, 159, 160, 162, 163, 164,
        166, 167, 168, 170, 171, 173, 174, 175, 177, 178,
        179, 181, 182, 184, 185, 186, 188, 189, 191, 192,
        193, 195, 196, 198, 199, 200, 202, 203, 205, 206,
        208, 209, 210, 212, 213, 215, 216, 217, 219, 220,
        222, 223, 225, 226, 228, 229, 230, 232, 233, 235,
        236, 238, 239, 241, 242, 243, 245, 246, 248, 249,
        251, 252, 254, 255, 257, 258, 260, 261, 263, 264,
        265, 267, 268, 270, 271, 273, 274, 276, 277, 279
    ];

    public static MonsterKillReward Resolve(MonsterRuntimeSnapshot monster, int playerLevel) =>
        Resolve(monster.Definition.Tier, playerLevel);

    internal static MonsterKillReward Resolve(uint monsterTier, int playerLevel)
    {
        if (playerLevel >= PlayerExperienceCatalog.MaximumLevel)
        {
            return new MonsterKillReward(0, 0);
        }

        var tier = Math.Clamp((int)Math.Min(monsterTier, int.MaxValue), 1, MonsterExperience.Length);
        playerLevel = Math.Max(1, playerLevel);
        var baseExperience = MonsterExperience[tier - 1] * CapturedNormalMonsterMultiplier;
        var levelsAboveMonster = Math.Max(0, playerLevel - tier);
        var experience = levelsAboveMonster >= 10
            ? 0
            : (baseExperience * (10 - levelsAboveMonster)) / 10;

        return experience > 0
            ? new MonsterKillReward(experience, NormalTalentExperience)
            : new MonsterKillReward(0, 0);
    }
}

internal readonly record struct MonsterKillReward(
    int Experience,
    int TalentExperience);
