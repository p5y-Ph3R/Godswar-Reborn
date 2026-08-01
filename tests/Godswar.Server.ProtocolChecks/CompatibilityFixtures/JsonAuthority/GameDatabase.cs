namespace Godswar.Server.State;

internal sealed class GameDatabase
{
    public int NextAccountId { get; set; } = 1;

    public int NextCharacterId { get; set; } = 1;

    public List<GameAccount> Accounts { get; set; } = [];

    public List<GameCharacter> Characters { get; set; } = [];

    public List<GameCharacterTalent> CharacterTalents { get; set; } = [];

    public List<CharacterExperienceBoost> CharacterExperienceBoosts { get; set; } = [];

    public List<FactionAreaExperienceControl> FactionAreaExperienceControls { get; set; } = [];
}

internal sealed class GameCharacterTalent
{
    public int CharacterId { get; set; }

    public int TalentId { get; set; }

    public int Rank { get; set; }
}

internal sealed class CharacterExperienceBoost
{
    public int CharacterId { get; set; }

    public int StatusId { get; set; }

    public int Kind { get; set; }

    public int BonusBasisPoints { get; set; }

    public int Priority { get; set; }

    public DateTimeOffset ActivatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Authoritative duration for a character-owned boost. Null means that the
    /// boost is permanent. This budget is consumed only by confirmed online
    /// intervals; ExpiresAt is retained solely for legacy-save migration.
    /// </summary>
    public long? RemainingOnlineTicks { get; set; }

    public string Source { get; set; } = string.Empty;
}

internal static class CharacterBoostOnlineDuration
{
    public static bool RestoreLegacyGrant(CharacterExperienceBoost boost)
    {
        if (boost.RemainingOnlineTicks.HasValue || !boost.ExpiresAt.HasValue)
        {
            return false;
        }

        boost.RemainingOnlineTicks = Math.Max(
            0L,
            (boost.ExpiresAt.Value - boost.ActivatedAt).Ticks);
        return true;
    }

    public static long RemainingTicks(CharacterExperienceBoost boost)
    {
        RestoreLegacyGrant(boost);
        return Math.Max(0L, boost.RemainingOnlineTicks ?? long.MaxValue);
    }

    public static void Consume(
        CharacterExperienceBoost boost,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil)
    {
        RestoreLegacyGrant(boost);
        if (!boost.RemainingOnlineTicks.HasValue ||
            boost.RemainingOnlineTicks.Value <= 0 ||
            onlineUntil <= onlineFrom ||
            onlineUntil <= boost.ActivatedAt)
        {
            return;
        }

        var effectiveFrom = onlineFrom > boost.ActivatedAt
            ? onlineFrom
            : boost.ActivatedAt;
        var consumedTicks = Math.Max(0L, (onlineUntil - effectiveFrom).Ticks);
        boost.RemainingOnlineTicks = Math.Max(
            0L,
            boost.RemainingOnlineTicks.Value - consumedTicks);
    }

    public static DateTimeOffset? EffectiveExpiry(
        CharacterExperienceBoost boost,
        DateTimeOffset now)
    {
        RestoreLegacyGrant(boost);
        if (!boost.RemainingOnlineTicks.HasValue)
        {
            return null;
        }

        var remainingTicks = Math.Max(0L, boost.RemainingOnlineTicks.Value);
        return now + TimeSpan.FromTicks(remainingTicks);
    }
}
