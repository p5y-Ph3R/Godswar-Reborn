using System.Collections.Frozen;

namespace Godswar.Server.Application.Items;

internal sealed class PinnedHolySuitContentCatalog :
    IHolySuitContentCatalog
{
    private readonly FrozenDictionary<uint, HolySuitConsumableDefinition>
        _consumablesByItemId;
    private readonly FrozenDictionary<(short Type, short Level),
        HolySuitUpgradeDefinition> _upgradesByCurrentState;

    private PinnedHolySuitContentCatalog(
        ItemTemplateDefinition[] itemTemplates,
        HolySuitTierDefinition[] tiers,
        HolySuitUpgradeDefinition[] upgrades,
        HolySuitConsumableDefinition[] consumables,
        HolySuitOperationPolicy? operationPolicy)
    {
        ItemTemplates = Array.AsReadOnly(itemTemplates);
        Tiers = Array.AsReadOnly(tiers);
        Upgrades = Array.AsReadOnly(upgrades);
        Consumables = Array.AsReadOnly(consumables);
        OperationPolicy = operationPolicy;
        _consumablesByItemId = consumables.ToFrozenDictionary(
            static value => value.ItemId);
        _upgradesByCurrentState = upgrades.ToFrozenDictionary(
            static value => (value.CurrentSuitType, value.CurrentLevel));
    }

    public static PinnedHolySuitContentCatalog Empty { get; } = new(
        [], [], [], [], null);

    public bool IsAvailable => OperationPolicy is not null;

    public IReadOnlyList<ItemTemplateDefinition> ItemTemplates { get; }

    public IReadOnlyList<HolySuitTierDefinition> Tiers { get; }

    public IReadOnlyList<HolySuitUpgradeDefinition> Upgrades { get; }

    public IReadOnlyList<HolySuitConsumableDefinition> Consumables { get; }

    public HolySuitOperationPolicy? OperationPolicy { get; }

    public bool TryGetConsumable(
        uint itemId,
        out HolySuitConsumableDefinition definition) =>
        _consumablesByItemId.TryGetValue(itemId, out definition!);

    public bool TryGetUpgrade(
        short currentSuitType,
        short currentLevel,
        out HolySuitUpgradeDefinition definition) =>
        _upgradesByCurrentState.TryGetValue(
            (currentSuitType, currentLevel),
            out definition!);

    public static PinnedHolySuitContentCatalog Create(
        IReadOnlyList<ItemTemplateDefinition> allItemTemplates,
        IReadOnlyList<HolySuitTierDefinition> tiers,
        IReadOnlyList<HolySuitUpgradeDefinition> upgrades,
        IReadOnlyList<HolySuitConsumableDefinition> consumables,
        HolySuitOperationPolicy operationPolicy)
    {
        ArgumentNullException.ThrowIfNull(allItemTemplates);
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(upgrades);
        ArgumentNullException.ThrowIfNull(consumables);
        ArgumentNullException.ThrowIfNull(operationPolicy);

        var tierSnapshot = tiers
            .OrderBy(static value => value.SuitType)
            .ToArray();
        var upgradeSnapshot = upgrades
            .OrderBy(static value => value.CurrentSuitType)
            .ThenBy(static value => value.CurrentLevel)
            .ToArray();
        var consumableSnapshot = consumables
            .OrderBy(static value => value.ItemId)
            .ToArray();
        Validate(tierSnapshot, upgradeSnapshot, consumableSnapshot,
            operationPolicy);

        var itemById = allItemTemplates.ToDictionary(
            static value => value.Id);
        var itemSnapshot = consumableSnapshot.Select(value =>
        {
            if (!itemById.TryGetValue(value.ItemId, out var item))
            {
                throw new InvalidOperationException(
                    $"Holy Suit item {value.ItemId} has no template.");
            }

            return item with
            {
                ClassIds = Array.AsReadOnly(item.ClassIds.ToArray())
            };
        }).ToArray();

        return new PinnedHolySuitContentCatalog(
            itemSnapshot,
            tierSnapshot,
            upgradeSnapshot,
            consumableSnapshot,
            operationPolicy);
    }

    private static void Validate(
        HolySuitTierDefinition[] tiers,
        HolySuitUpgradeDefinition[] upgrades,
        HolySuitConsumableDefinition[] consumables,
        HolySuitOperationPolicy policy)
    {
        if (tiers.Length != 8 ||
            tiers.Select(static value => value.SuitType)
                .Distinct().Count() != tiers.Length ||
            tiers[0] is not { SuitType: 0, MaxLevel: 0,
                WareItemId: null } ||
            tiers.Skip(1).Any(static value =>
                value.SuitType is < 1 or > 7 ||
                value.MaxLevel != 10 ||
                value.WareItemId is null ||
                string.IsNullOrWhiteSpace(value.Name) ||
                string.IsNullOrWhiteSpace(value.Source)))
        {
            throw new InvalidOperationException(
                "Holy Suit tiers must define Common plus seven 10-level tiers.");
        }

        if (consumables.Length != 13 ||
            consumables.Select(static value => value.ItemId)
                .Distinct().Count() != consumables.Length ||
            consumables.Any(static value =>
                value.ItemId is < 9010 or > 9025 ||
                value.ItemId is > 9016 and < 9020 ||
                value.StackCap <= 0 ||
                value.GrantedBound is < 0 or > 1 ||
                string.IsNullOrWhiteSpace(value.Source)))
        {
            throw new InvalidOperationException(
                "Holy Suit consumables must be the reviewed 9010-9025 set.");
        }

        var wareByTier = consumables
            .Where(static value =>
                value.Role == HolySuitConsumableRole.Ware &&
                value.SuitType.HasValue)
            .ToDictionary(static value => value.SuitType!.Value);
        if (wareByTier.Count != 7 || tiers.Skip(1).Any(tier =>
                !wareByTier.TryGetValue(tier.SuitType, out var ware) ||
                ware.ItemId != tier.WareItemId ||
                ware.ExperienceCapacity != 0 ||
                ware.StackCap != 99))
        {
            throw new InvalidOperationException(
                "Each Holy Suit tier must reference its matching ware.");
        }

        if (consumables.Count(static value =>
                value.Role == HolySuitConsumableRole.HolyBox) != 5 ||
            consumables.Count(static value =>
                value.Role == HolySuitConsumableRole.ExperiencePrism) != 1)
        {
            throw new InvalidOperationException(
                "Holy Suit content requires five boxes and one prism.");
        }

        ValidateUpgradeChain(upgrades, wareByTier);
        if (!IsSupportedOperationPolicy(policy))
        {
            throw new InvalidOperationException(
                "The Holy Suit operation policy is not a supported sealed " +
                "alpha revision.");
        }
    }

    internal static bool IsSupportedOperationPolicy(
        HolySuitOperationPolicy policy)
    {
        if (policy.MinimumPlayerLevel != 70 ||
            policy.MinimumGearLevel != 70 ||
            policy.LegacyDailyExperiencePerPlayerLevel != 1_000_000 ||
            policy.GearExperienceCapacity != 2_000_000_000 ||
            policy.ExperiencePrismCost != 100_000_000 ||
            !policy.DailyQuotaBypassEntitlement.Equals(
                "battle_pass", StringComparison.Ordinal))
        {
            return false;
        }

        var legacy =
            policy.PerOperationExperienceMaximum == 100_000_000 &&
            policy.DailyExperiencePerPlayer is null &&
            policy.RealmDayTimeZone.Equals("UTC", StringComparison.Ordinal) &&
            policy.Source.Equals(
                "alpha-policy-2026-08-01", StringComparison.Ordinal);
        var fixedDailyCap =
            policy.PerOperationExperienceMaximum == 100_000_000 &&
            policy.DailyExperiencePerPlayer == 2_000_000_000 &&
            policy.RealmDayTimeZone.Equals(
                "Asia/Singapore", StringComparison.Ordinal) &&
            policy.Source.Equals(
                "alpha-policy-2026-08-02", StringComparison.Ordinal);
        var boxCapacityLimit =
            policy.PerOperationExperienceMaximum == 400_000_000 &&
            policy.DailyExperiencePerPlayer == 2_000_000_000 &&
            policy.RealmDayTimeZone.Equals(
                "Asia/Singapore", StringComparison.Ordinal) &&
            policy.Source.Equals(
                "alpha-policy-2026-08-02-box-capacity",
                StringComparison.Ordinal);
        return legacy || fixedDailyCap || boxCapacityLimit;
    }

    private static void ValidateUpgradeChain(
        HolySuitUpgradeDefinition[] upgrades,
        IReadOnlyDictionary<short, HolySuitConsumableDefinition> wareByTier)
    {
        if (upgrades.Length != 70 ||
            upgrades.Select(static value =>
                    (value.CurrentSuitType, value.CurrentLevel))
                .Distinct().Count() != upgrades.Length)
        {
            throw new InvalidOperationException(
                "Holy Suit content must define exactly 70 unique upgrades.");
        }

        short currentType = 0;
        short currentLevel = 0;
        foreach (var upgrade in upgrades)
        {
            if (upgrade.CurrentSuitType != currentType ||
                upgrade.CurrentLevel != currentLevel ||
                upgrade.TargetSuitType is < 1 or > 7 ||
                upgrade.TargetLevel is < 1 or > 10 ||
                upgrade.WareQuantity != upgrade.TargetLevel ||
                !wareByTier.TryGetValue(upgrade.TargetSuitType, out var ware) ||
                ware.ItemId != upgrade.WareItemId ||
                upgrade.RequiredItemExperience < 0 ||
                upgrade.RequiredPrisms < 0 ||
                (upgrade.TargetSuitType < 5 &&
                    (upgrade.RequiredItemExperience <= 0 ||
                     upgrade.RequiredPrisms != 0)) ||
                (upgrade.TargetSuitType >= 5 &&
                 upgrade.CurrentSuitType >= 5 &&
                    (upgrade.RequiredItemExperience != 0 ||
                     upgrade.RequiredPrisms <= 0)))
            {
                throw new InvalidOperationException(
                    $"Holy Suit upgrade {currentType}:{currentLevel} is invalid.");
            }

            currentType = upgrade.TargetSuitType;
            currentLevel = upgrade.TargetLevel;
        }

        if (currentType != 7 || currentLevel != 10)
        {
            throw new InvalidOperationException(
                "Holy Suit upgrade chain must end at Adamantium level 10.");
        }
    }
}
