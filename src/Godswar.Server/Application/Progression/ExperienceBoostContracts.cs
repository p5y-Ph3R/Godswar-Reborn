using System.Collections.Immutable;

namespace Godswar.Server.Application.Progression;

internal interface IExperienceBoostStateReader
{
    Task<ExperienceBoostSnapshot> ReadAsync(
        ExperienceBoostReadRequest request,
        CancellationToken cancellationToken = default);
}

internal readonly record struct ExperienceBoostReadRequest(
    int AccountId,
    int CharacterId,
    byte Camp,
    short MapId,
    DateTimeOffset ReadAtUtc);

internal sealed record ExperienceBoostEntry(
    int StatusId,
    int Kind,
    int BonusBasisPoints,
    int Priority,
    DateTimeOffset? ExpiresAtUtc,
    string Source)
{
    public uint RemainingSeconds(DateTimeOffset nowUtc)
    {
        ExperienceBoostContract.RequireUtc(nowUtc, nameof(nowUtc));
        if (Kind == ExperienceBoostKinds.Vip ||
            ExpiresAtUtc is null)
        {
            return uint.MaxValue;
        }

        return (uint)Math.Clamp(
            (long)Math.Ceiling(
                (ExpiresAtUtc.Value - nowUtc).TotalSeconds),
            0L,
            uint.MaxValue);
    }
}

internal sealed record ExperienceBoostSnapshot(
    ImmutableArray<ExperienceBoostEntry> ActiveBoosts)
{
    public static ExperienceBoostSnapshot Empty { get; } =
        new(ImmutableArray<ExperienceBoostEntry>.Empty);

    public int TotalBonusBasisPoints => TotalFor(
        static boost => boost.Kind != ExperienceBoostKinds.Talent);

    public int TotalTalentBonusBasisPoints => TotalFor(
        static boost => boost.Kind == ExperienceBoostKinds.Talent);

    public int ApplyTo(int baseExperience) =>
        ApplyBonus(baseExperience, TotalBonusBasisPoints);

    public int ApplyToTalent(int baseTalentExperience) =>
        ApplyBonus(
            baseTalentExperience,
            TotalTalentBonusBasisPoints);

    private int TotalFor(Func<ExperienceBoostEntry, bool> predicate)
    {
        var total = ActiveBoosts
            .Where(predicate)
            .Aggregate(
                0L,
                static (sum, boost) =>
                    sum + boost.BonusBasisPoints);
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    private static int ApplyBonus(
        int baseExperience,
        int bonusBasisPoints)
    {
        if (baseExperience <= 0)
        {
            return 0;
        }

        var multiplierBasisPoints =
            Math.Max(0L, 10_000L + bonusBasisPoints);
        var adjusted =
            ((long)baseExperience * multiplierBasisPoints) / 10_000L;
        return (int)Math.Min(adjusted, int.MaxValue);
    }
}

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
    public const int TalentExperience50Percent = 580;
    public const int TalentPotion50Percent = 587;
    public const int TalentExperience100Percent = 581;
    public const int HighTalentBoost100Percent = 509;
    public const int TalentExperience200Percent = 582;
    public const int SuperTalentPotion200Percent = 588;
    public const int TalentExperience300Percent = 583;
    public const int IncredibleTalentPotion300Percent = 589;
    public const int TalentExperience400Percent = 584;
    public const int MaxTalentPotion400Percent = 590;
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
    public const int Talent = 20;
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

internal static class ExperienceBoostContract
{
    public const int MaximumActiveBoosts = 66;
    public const int MaximumSourceLength = 160;

    public static void ValidateRequest(ExperienceBoostReadRequest request)
    {
        if (request.AccountId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "An experience-boost read requires a positive account ID.");
        }

        if (request.CharacterId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "An experience-boost read requires a positive character ID.");
        }

        if (request.Camp > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The character camp must be Sparta or Athens.");
        }

        if (request.MapId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The map ID cannot be negative.");
        }

        RequireUtc(request.ReadAtUtc, nameof(request));
    }

    public static void ValidateSnapshot(
        ExperienceBoostSnapshot snapshot,
        DateTimeOffset readAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireUtc(readAtUtc, nameof(readAtUtc));
        if (snapshot.ActiveBoosts.IsDefault ||
            snapshot.ActiveBoosts.Length > MaximumActiveBoosts)
        {
            throw new InvalidDataException(
                "The experience-boost projection exceeds its row bound.");
        }

        var previousKind = int.MinValue;
        foreach (var boost in snapshot.ActiveBoosts)
        {
            if (boost is null ||
                boost.StatusId <= 0 ||
                boost.Kind <= 0 ||
                boost.BonusBasisPoints < 0 ||
                boost.Priority < 0 ||
                boost.Source is null ||
                boost.Source.Length > MaximumSourceLength ||
                boost.Kind <= previousKind)
            {
                throw new InvalidDataException(
                    "The experience-boost projection contains invalid or duplicate rows.");
            }

            if (boost.ExpiresAtUtc is { } expiresAt)
            {
                if (expiresAt == default ||
                    expiresAt.Offset != TimeSpan.Zero ||
                    expiresAt <= readAtUtc)
                {
                    throw new InvalidDataException(
                        "An active experience boost has an invalid expiry.");
                }
            }

            previousKind = boost.Kind;
        }
    }

    internal static void RequireUtc(
        DateTimeOffset value,
        string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The timestamp must be a non-default UTC value.");
        }
    }
}
