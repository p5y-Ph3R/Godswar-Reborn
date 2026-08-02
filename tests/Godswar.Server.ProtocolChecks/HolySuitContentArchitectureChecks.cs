using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class HolySuitContentArchitectureChecks
{
    public const string CheckName =
        "Revision-pinned Holy Suit content and durable policy schema";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static value => value.Id ==
                "20260801_046_holy_suit_content_release");
        AssertContains(
            migration.Sql,
            "manifest_version IN (1, 2, 3, 4, 5)",
            "holy_suit_tier_count = 8",
            "holy_suit_upgrade_count = 70",
            "holy_suit_consumable_count = 13",
            "holy_suit_operation_policy_content_definitions",
            "account_entitlements",
            "holy_suit_daily_exp_storage",
            "recompute_character_holy_suit_points",
            "item.slot_index BETWEEN 0 AND 11");
        var fixedCapMigration = PostgresSchemaMigrationCatalog.All.Single(
            static value => value.Id ==
                "20260802_050_holy_suit_fixed_daily_cap");
        AssertContains(
            fixedCapMigration.Sql,
            "ADD COLUMN daily_experience_per_player bigint",
            "daily_experience_per_player IS NULL",
            "BETWEEN 1 AND 4294967295",
            "official_holy_suit_operation_policy_content");

        Check.Equal(13, HolySuitContentBaseline.ItemTemplates.Count,
            "Holy Suit publication contains 13 original-client items");
        Check.True(
            HolySuitContentBaseline.ItemTemplates
                .Select(static value => value.Id)
                .SequenceEqual([
                    9010, 9011, 9012, 9013, 9014, 9015, 9016,
                    9020, 9021, 9022, 9023, 9024, 9025
                ]),
            "Holy Suit publication uses exact original-client item IDs");

        var boxes = HolySuitContentBaseline.Consumables
            .Where(static value =>
                value.Role == HolySuitConsumableRole.HolyBox)
            .Select(static value => value.ExperienceCapacity)
            .ToArray();
        Check.True(
            boxes.SequenceEqual([
                100_000L,
                1_000_000L,
                10_000_000L,
                100_000_000L,
                400_000_000L
            ]),
            "Holy Box capacities match the original client");

        Check.Equal(70, HolySuitContentBaseline.Upgrades.Count,
            "Holy Suit publication has every upgrade transition");
        var initial = HolySuitContentBaseline.Upgrades[0];
        Check.True(
            initial is
            {
                CurrentSuitType: 0,
                CurrentLevel: 0,
                TargetSuitType: 1,
                TargetLevel: 1,
                WareItemId: 9010,
                WareQuantity: 1
            },
            "upgrade chain normalizes Common to Bronze level 1");
        Check.Equal(
            5_649_898L,
            FindUpgrade(2, 1).RequiredItemExperience,
            "Silver level 1 to 2 uses corrected EquipEffect cost");
        Check.Equal(
            65_349_705L,
            FindUpgrade(3, 2).RequiredItemExperience,
            "Gold level 2 to 3 uses corrected EquipEffect cost");
        Check.True(
            HolySuitContentBaseline.Upgrades.All(static value =>
                value.WareQuantity == value.TargetLevel &&
                value.WareItemId == 9009u + value.TargetSuitType),
            "every upgrade consumes target-tier ware in target-level quantity");

        var policy = HolySuitContentBaseline.OperationPolicy;
        Check.True(
            policy.MinimumPlayerLevel == 70 &&
            policy.MinimumGearLevel == 70 &&
            policy.LegacyDailyExperiencePerPlayerLevel == 1_000_000 &&
            policy.DailyExperiencePerPlayer == 2_000_000_000 &&
            policy.PerOperationExperienceMaximum == 400_000_000 &&
            policy.GearExperienceCapacity == 2_000_000_000 &&
            policy.ExperiencePrismCost == 100_000_000 &&
            policy.RealmDayTimeZone == "Asia/Singapore" &&
            policy.DailyQuotaBypassEntitlement == "battle_pass",
            "alpha Holy Suit policy is explicit and complete");
        Check.Equal(
            2_000_000_000L,
            policy.ResolveDailyExperienceLimit(70),
            "fixed daily allowance is independent of player level");
        Check.Equal(
            2_000_000_000L,
            policy.ResolveDailyExperienceLimit(120),
            "maximum-level player receives the same fixed allowance");

        var legacy = policy with
        {
            DailyExperiencePerPlayer = null,
            PerOperationExperienceMaximum = 100_000_000,
            RealmDayTimeZone = "UTC",
            Source = "alpha-policy-2026-08-01"
        };
        var previousFixedCap = policy with
        {
            PerOperationExperienceMaximum = 100_000_000,
            Source = "alpha-policy-2026-08-02"
        };
        Check.True(
            PinnedHolySuitContentCatalog.IsSupportedOperationPolicy(legacy) &&
            PinnedHolySuitContentCatalog.IsSupportedOperationPolicy(
                previousFixedCap) &&
            PinnedHolySuitContentCatalog.IsSupportedOperationPolicy(policy) &&
            legacy.ResolveDailyExperienceLimit(80) == 80_000_000,
            "runtime can verify legacy, fixed-cap, and box-capacity revisions");
        Check.True(
            !PinnedHolySuitContentCatalog.IsSupportedOperationPolicy(
                policy with { RealmDayTimeZone = "UTC" }),
            "mixed fixed-cap and legacy-time-zone policy is rejected");
        return Task.CompletedTask;
    }

    private static HolySuitUpgradeDefinition FindUpgrade(
        short type,
        short level) => HolySuitContentBaseline.Upgrades.Single(
            value => value.CurrentSuitType == type &&
                     value.CurrentLevel == level);

    private static void AssertContains(string value, params string[] parts)
    {
        foreach (var part in parts)
        {
            Check.True(
                value.Contains(part, StringComparison.OrdinalIgnoreCase),
                $"Holy Suit migration contains {part}");
        }
    }
}
