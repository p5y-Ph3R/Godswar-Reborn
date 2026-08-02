namespace Godswar.Server.State;

internal static class PlayerExperienceCatalog
{
    internal const int MaximumLevel = 200;
    internal const int FighterLevelSealLevel = 89;
    internal const long MaximumStoredExperience = uint.MaxValue;

    // Original server PlayerNextGradeExp table. Entry n is the fighter EXP
    // needed to advance from level n. The level-200 entry is retained as
    // progression metadata, but the client bar uses the UInt32 ceiling there.
    private static readonly int[] NextLevelExperience =
    [
        200, 252, 286, 345, 432, 500, 598, 729, 840, 986,
        1170, 1408, 1650, 1904, 2205, 2556, 2923, 3382, 3861, 4400,
        5043, 5754, 6536, 7392, 8370, 9682, 10896, 12299, 13800, 15504,
        17368, 19451, 21708, 24696, 27474, 30566, 33984, 37740, 41785, 46252,
        51968, 57395, 63294, 69747, 76772, 84387, 94004, 103176, 113077, 123802,
        135450, 149996, 163800, 178619, 194720, 212058, 233562, 253932, 275910, 299452,
        328504, 356089, 385560, 417326, 456165, 492936, 532380, 574464, 625926, 674586,
        726500, 781942, 849441, 913120, 981015, 1053322, 1140912, 1223525, 1311420, 1404927,
        1517816, 1624158, 1737075, 1873053, 2001162, 2137002, 2280960, 2453664, 2616333, 2788636,
        2994516, 3188589, 3393792, 3638570, 3869216, 4112724, 4402570, 4675590, 4963456, 5266965,
        5627415, 5966660, 6323850, 6747169, 7145568, 7564650, 8060451, 8526724, 9016735, 9595295,
        10139464, 10780924, 11384130, 12017304, 12762292, 13462848, 14197280, 15060168, 15871473, 16721276,
        17718010, 18654902, 19635504, 20784030, 21863205, 23125429, 24311802, 25551750, 27000288, 28361096,
        29782557, 31440786, 32998056, 34812984, 36517520, 38296098, 40365668, 42309351, 44569086, 46690944,
        48902340, 51470250, 53880988, 56678094, 59303990, 62037400, 65205196, 68178565, 71621260, 74852778,
        78590928, 82099798, 85747200, 89962200, 93917664, 98485050, 102771288, 107222304, 112357074, 117174640,
        122727594, 127937553, 133938450, 139568334, 146048136, 152127219, 158430440, 165678624, 172477318, 180290120,
        187618112, 196033460, 203926272, 212984232, 221479852, 230275548, 240361170, 249818904, 260656928, 270819870,
        282458834, 293372604, 305863752, 317576235, 330973338, 343535256, 357895460, 371360196, 386743341, 401166744,
        416066165, 433077471, 449024084, 467220960, 484278355, 503732229, 521967534, 542754000, 562237703, 584435250
    ];

    public static int GetNextLevelExperience(int level) =>
        NextLevelExperience[Math.Clamp(level, 1, MaximumLevel) - 1];

    public static long GetClientExperienceMaximum(
        int level,
        bool fighterLevelSealed)
    {
        if (level is < 1 or > MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                $"Fighter level must be between 1 and {MaximumLevel}.");
        }

        if (fighterLevelSealed)
        {
            if (level != FighterLevelSealLevel)
            {
                throw new InvalidOperationException(
                    $"Fighter level sealing is valid only at level {FighterLevelSealLevel}.");
            }

            return MaximumStoredExperience;
        }

        return level == MaximumLevel
            ? MaximumStoredExperience
            : GetNextLevelExperience(level);
    }

    public static PlayerExperienceProgression Apply(
        int level,
        long currentExperience,
        int gainedExperience,
        bool fighterLevelSealed = false)
    {
        level = Math.Clamp(level, 1, MaximumLevel);
        currentExperience = Math.Clamp(
            currentExperience,
            0L,
            MaximumStoredExperience);
        gainedExperience = Math.Max(0, gainedExperience);

        if (fighterLevelSealed)
        {
            if (level != FighterLevelSealLevel)
            {
                throw new InvalidOperationException(
                    $"Fighter level sealing is valid only at level {FighterLevelSealLevel}.");
            }

            var availableCredit =
                MaximumStoredExperience - currentExperience;
            var creditedExperience = checked((int)Math.Min(
                gainedExperience,
                availableCredit));
            return new PlayerExperienceProgression(
                level,
                currentExperience + creditedExperience,
                creditedExperience,
                []);
        }

        if (level >= MaximumLevel || gainedExperience == 0)
        {
            return new PlayerExperienceProgression(
                level,
                currentExperience,
                level >= MaximumLevel ? 0 : gainedExperience,
                []);
        }

        var accumulatedExperience = currentExperience + gainedExperience;
        var levelUps = new List<PlayerLevelUpProgression>();
        while (level < MaximumLevel && accumulatedExperience >= GetNextLevelExperience(level))
        {
            accumulatedExperience -= GetNextLevelExperience(level);
            level++;
            levelUps.Add(new PlayerLevelUpProgression(
                level,
                Math.Min(
                    accumulatedExperience,
                    MaximumStoredExperience),
                GetNextLevelExperience(level)));
        }

        return new PlayerExperienceProgression(
            level,
            Math.Min(accumulatedExperience, MaximumStoredExperience),
            gainedExperience,
            levelUps);
    }
}

internal sealed record PlayerExperienceProgression(
    int Level,
    long Experience,
    int ExperienceGained,
    IReadOnlyList<PlayerLevelUpProgression> LevelUps);

internal readonly record struct PlayerLevelUpProgression(
    int Level,
    long CurrentExperience,
    int NextLevelExperience);
