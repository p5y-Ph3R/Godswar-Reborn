namespace Godswar.Server.Application.Items;

internal enum HolySuitConsumableRole : short
{
    Ware = 1,
    HolyBox = 2,
    ExperiencePrism = 3
}

internal sealed record HolySuitTierDefinition(
    short SuitType,
    string Name,
    short MaxLevel,
    uint? WareItemId,
    string Source);

internal sealed record HolySuitUpgradeDefinition(
    short CurrentSuitType,
    short CurrentLevel,
    short TargetSuitType,
    short TargetLevel,
    long RequiredItemExperience,
    uint WareItemId,
    short WareQuantity,
    int RequiredPrisms,
    string Source);

internal sealed record HolySuitConsumableDefinition(
    uint ItemId,
    HolySuitConsumableRole Role,
    short? SuitType,
    long ExperienceCapacity,
    short StackCap,
    short GrantedBound,
    string Source);

internal sealed record HolySuitOperationPolicy(
    short MinimumPlayerLevel,
    short MinimumGearLevel,
    long LegacyDailyExperiencePerPlayerLevel,
    long? DailyExperiencePerPlayer,
    long PerOperationExperienceMaximum,
    long GearExperienceCapacity,
    long ExperiencePrismCost,
    string RealmDayTimeZone,
    string DailyQuotaBypassEntitlement,
    string Source)
{
    // Retained only so old sealed manifest-v5 revisions can be verified and
    // upgraded without rewriting their hash input.
    public long DailyExperiencePerPlayerLevel =>
        LegacyDailyExperiencePerPlayerLevel;

    public long ResolveDailyExperienceLimit(int playerLevel)
    {
        if (DailyExperiencePerPlayer.HasValue)
        {
            return DailyExperiencePerPlayer.Value;
        }

        return checked(
            (long)playerLevel * LegacyDailyExperiencePerPlayerLevel);
    }
}

/// <summary>
/// Revision-pinned Holy Suit policy. The item definitions and every rule in
/// this catalog are sealed in the same item-content manifest.
/// </summary>
internal interface IHolySuitContentCatalog
{
    bool IsAvailable { get; }

    IReadOnlyList<ItemTemplateDefinition> ItemTemplates { get; }

    IReadOnlyList<HolySuitTierDefinition> Tiers { get; }

    IReadOnlyList<HolySuitUpgradeDefinition> Upgrades { get; }

    IReadOnlyList<HolySuitConsumableDefinition> Consumables { get; }

    HolySuitOperationPolicy? OperationPolicy { get; }

    bool TryGetConsumable(
        uint itemId,
        out HolySuitConsumableDefinition definition);

    bool TryGetUpgrade(
        short currentSuitType,
        short currentLevel,
        out HolySuitUpgradeDefinition definition);
}
