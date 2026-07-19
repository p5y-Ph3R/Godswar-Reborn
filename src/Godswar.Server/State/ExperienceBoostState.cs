namespace Godswar.Server.State;

internal enum VipTier : short
{
    None = 0,
    Bronze = 1,
    Silver = 2,
    Gold = 3,
    Platinum = 4
}

internal static class ExperienceStatusIds
{
    public const int Weekend = 511;
    public const int TrickOrTreat = 512;
    public const int MaxExperiencePotion = 586;
    public const int GuildDoubleExperience16Hours = 1007;
    public const int VipBronze = 1500;
    public const int VipSilver = 1501;
    public const int VipGold = 1502;
    public const int VipPlatinum = 1503;
    public const int FactionAreaExperience = 1504;
}

internal static class ExperienceBoostKinds
{
    public const int Consumable = 14;
    public const int Weekend = 22;
    public const int TrickOrTreat = 23;
    public const int Guild = 100;
    public const int Vip = 1008;
    public const int FactionArea = 1009;
}

internal static class VipExperienceBoosts
{
    public static int BonusBasisPoints(VipTier tier) => tier switch
    {
        VipTier.Bronze => 500,
        VipTier.Silver => 1_000,
        VipTier.Gold => 1_500,
        VipTier.Platinum => 2_000,
        _ => 0
    };

    public static int StatusId(VipTier tier) => tier switch
    {
        VipTier.Bronze => ExperienceStatusIds.VipBronze,
        VipTier.Silver => ExperienceStatusIds.VipSilver,
        VipTier.Gold => ExperienceStatusIds.VipGold,
        VipTier.Platinum => ExperienceStatusIds.VipPlatinum,
        _ => 0
    };
}

internal sealed record ActiveExperienceBoost(
    int StatusId,
    int Kind,
    int BonusBasisPoints,
    int Priority,
    DateTimeOffset? ExpiresAt,
    string Source)
{
    public uint RemainingSeconds(DateTimeOffset now)
    {
        // VIP membership can last longer than a practical countdown timer.
        // Its client definitions are permanent while present; periodic server
        // reconciliation removes the icon when the entitlement expires.
        if (Kind == ExperienceBoostKinds.Vip || ExpiresAt is null)
        {
            return uint.MaxValue;
        }

        return (uint)Math.Clamp(
            (long)Math.Ceiling((ExpiresAt.Value - now).TotalSeconds),
            0L,
            uint.MaxValue);
    }
}

internal sealed record ExperienceBoostState(
    IReadOnlyList<ActiveExperienceBoost> ActiveBoosts)
{
    public static ExperienceBoostState Empty { get; } = new([]);

    public int TotalBonusBasisPoints
    {
        get
        {
            var total = ActiveBoosts.Aggregate(
                0L,
                static (sum, boost) => sum + boost.BonusBasisPoints);
            return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
        }
    }

    public int ApplyTo(int baseExperience)
    {
        if (baseExperience <= 0)
        {
            return 0;
        }

        var multiplierBasisPoints = Math.Max(0L, 10_000L + TotalBonusBasisPoints);
        var adjusted = ((long)baseExperience * multiplierBasisPoints) / 10_000L;
        return (int)Math.Min(adjusted, int.MaxValue);
    }
}

internal sealed record WorldBossRespawnState(
    short MapId,
    string BossTemplateKey,
    DateTimeOffset RespawnAt);
