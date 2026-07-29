namespace Godswar.Server.Application.Characters;

internal static class CharacterTalentProjection
{
    public const int RankCap = 100;

    public static CharacterTalentSnapshot FromPersistedRank(
        int talentId,
        int persistedRank)
    {
        var rank = Math.Max(0, persistedRank);
        return new CharacterTalentSnapshot(
            talentId,
            rank,
            CalculateDisplayValue(rank),
            CalculateUpgradeCost(rank));
    }

    public static int CalculateDisplayValue(int currentRank)
    {
        if (currentRank <= 0)
        {
            return 1;
        }

        return (CalculateEffectiveRankValue(currentRank) * 3) + 1;
    }

    public static int CalculateUpgradeCost(int currentRank)
    {
        var nextRank = Math.Max(0, currentRank) + 1;
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
            var offset = nextRank - 11;
            return 10 + (offset * 6) + Triangular(offset);
        }

        if (nextRank <= 60)
        {
            var offset = nextRank - 41;
            return 380 + (offset * 18) + Triangular(offset);
        }

        if (nextRank <= 80)
        {
            var offset = nextRank - 61;
            return 900 + (offset * 32) + (Triangular(offset) * 2);
        }

        if (nextRank <= 90)
        {
            var offset = nextRank - 81;
            return 1_900 + (offset * 65) + (Triangular(offset) * 5);
        }

        var highOffset = nextRank - 91;
        return 2_900 + (highOffset * 90) + (Triangular(highOffset) * 7);
    }

    private static int CalculateEffectiveRankValue(int currentRank)
    {
        var rank = Math.Clamp(currentRank, 0, RankCap);
        if (rank <= 40)
        {
            return rank;
        }

        if (rank <= 60)
        {
            return 40 + ((rank - 40) * 2);
        }

        if (rank <= 80)
        {
            return 80 + ((rank - 60) * 3);
        }

        if (rank <= 90)
        {
            return 140 + ((rank - 80) * 5);
        }

        return 190 + ((rank - 90) * 7);
    }

    private static int Triangular(int value) =>
        (value * (value + 1)) / 2;
}
