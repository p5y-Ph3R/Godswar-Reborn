namespace Godswar.Server.Application.Talents;

internal static class TalentProgression
{
    public const int RankCap = 100;

    public static int CalculateUpgradeCost(int currentRank)
    {
        var nextRank = NextRank(currentRank);
        if (nextRank > RankCap)
        {
            return 0;
        }

        if (nextRank <= 10)
        {
            return nextRank;
        }

        if (nextRank <= 40)
        {
            var rankOffset = nextRank - 11;
            return 10 + (rankOffset * 6) + Triangular(rankOffset);
        }

        if (nextRank <= 60)
        {
            var rankOffset = nextRank - 41;
            return 380 + (rankOffset * 18) + Triangular(rankOffset);
        }

        if (nextRank <= 80)
        {
            var rankOffset = nextRank - 61;
            return 900 + (rankOffset * 32) + (Triangular(rankOffset) * 2);
        }

        if (nextRank <= 90)
        {
            var rankOffset = nextRank - 81;
            return 1900 + (rankOffset * 65) + (Triangular(rankOffset) * 5);
        }

        var highRankOffset = nextRank - 91;
        return 2900 + (highRankOffset * 90) + (Triangular(highRankOffset) * 7);
    }

    public static int CalculateRequiredPlayerLevel(int currentRank)
    {
        var nextRank = NextRank(currentRank);
        if (nextRank <= 40)
        {
            return Scale(nextRank, sourceMin: 1, sourceMax: 40, targetMin: 1, targetMax: 120);
        }

        if (nextRank <= 60)
        {
            return 120 + (nextRank - 40);
        }

        return 180;
    }

    public static int CalculateDisplayValue(int currentRank)
    {
        if (currentRank <= 0)
        {
            return 1;
        }

        return (CalculateEffectiveRankValue(currentRank) * 3) + 1;
    }

    public static int CalculateEffectiveRankValue(int currentRank)
    {
        var safeRank = Math.Clamp(currentRank, 0, RankCap);
        if (safeRank <= 40)
        {
            return safeRank;
        }

        if (safeRank <= 60)
        {
            return 40 + ((safeRank - 40) * 2);
        }

        if (safeRank <= 80)
        {
            return 80 + ((safeRank - 60) * 3);
        }

        if (safeRank <= 90)
        {
            return 140 + ((safeRank - 80) * 5);
        }

        return 190 + ((safeRank - 90) * 7);
    }

    private static int NextRank(int currentRank)
    {
        return Math.Max(0, currentRank) + 1;
    }

    private static int Triangular(int value)
    {
        return (value * (value + 1)) / 2;
    }

    private static int Scale(int value, int sourceMin, int sourceMax, int targetMin, int targetMax)
    {
        if (value <= sourceMin)
        {
            return targetMin;
        }

        if (value >= sourceMax)
        {
            return targetMax;
        }

        var sourceRange = sourceMax - sourceMin;
        var targetRange = targetMax - targetMin;
        return targetMin + (((value - sourceMin) * targetRange) / sourceRange);
    }
}
