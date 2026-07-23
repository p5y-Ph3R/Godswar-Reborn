using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;


internal static partial class EquipmentForgeCatalogChecks
{
    public static Task RunAsync()
    {
        CheckCatalogCoverage();
        CheckSilverEconomyVectors();
        CheckItemTemplateProgressionCoverage();
        CheckClassNeutralEquipmentRankProfiles();
        CheckMaterialRules();
        CheckRubyCalculation();
        CheckSapphireCalculation();
        CheckEmeraldCalculation();
        CheckValidation();
        return Task.CompletedTask;
    }

    private static void CheckCatalogCoverage()
    {
        Check.Equal(
            EquipmentForgeCatalog.ShippedRuleCount,
            EquipmentForgeCatalog.All.Count,
            "shipped equipment-forge rule count");
        Check.Equal(
            EquipmentForgeCatalog.All.Count,
            EquipmentForgeCatalog.All.Select(rule => rule.ItemId).Distinct().Count(),
            "equipment-forge IDs are unique");

        Check.True(EquipmentForgeCatalog.TryGet(1000, out var shortSword), "short sword forge rule resolves");
        Check.Equal(1001u, shortSword.NextItemId ?? 0, "short sword next ID");
        Check.Equal(1000u, shortSword.BadItemId ?? 0, "short sword bad ID");
        Check.Equal(95, shortSword.Probability ?? -1, "short sword base probability");
        Check.Equal(56, shortSword.Amoney, "short sword Ruby cost");
        Check.Equal(-215, shortSword.BaseProyAdd[8], "short sword Q9 probability adjustment");
        Check.Equal(-225, shortSword.BaseProyAdd[9], "short sword Q10 probability adjustment");
        Check.Equal(-235, shortSword.BaseProyAdd[10], "short sword Q11 probability adjustment");
        Check.Equal(-245, shortSword.BaseProyAdd[11], "short sword Q12 probability adjustment");
        Check.Equal(-315, shortSword.BaseProyAdd[18], "short sword Q19 probability adjustment");
        Check.Equal(0, shortSword.BaseProyAdd[19], "short sword Q20 probability terminal sentinel");
        Check.Equal(-220, shortSword.AppendProyAdd[10], "short sword G11 probability adjustment");
        Check.Equal(-545, shortSword.AppendProyAdd[23], "short sword G24 probability adjustment");
        Check.Equal(0, shortSword.AppendProyAdd[24], "short sword G25 probability terminal sentinel");
        Check.Equal(18, shortSword.Bmoney[8], "short sword Q9 cost");
        Check.Equal(20, shortSword.Bmoney[9], "short sword Q10 cost");
        Check.Equal(25, shortSword.Bmoney[10], "short sword Q11 cost");
        Check.Equal(30, shortSword.Bmoney[11], "short sword Q12 cost");
        Check.Equal(21, shortSword.Cmoney[10], "short sword G11 cost");

        Check.True(EquipmentForgeCatalog.TryGet(1001, out var scimitar), "scimitar forge rule resolves");
        Check.Equal(400, scimitar.Bmoney[9], "level-20 economy Q10 cost");
        Check.Equal(500, scimitar.Bmoney[10], "level-20 economy Q11 cost");
        Check.Equal(600, scimitar.Bmoney[11], "level-20 economy Q12 cost");

        Check.True(EquipmentForgeCatalog.TryGet(1435, out var extended), "extended spear rule resolves");
        Check.Equal(20, extended.BaseProyAdd.Count, "extended spear Q20 rule length");
        Check.Equal(25, extended.AppendProyAdd.Count, "extended spear G25 rule length");

        Check.True(EquipmentForgeCatalog.TryGet(1499, out var custom), "custom spear rule resolves");
        Check.Equal(-235, custom.BaseProyAdd[10], "custom spear Q11 probability adjustment");
        Check.Equal(-270, custom.AppendProyAdd[12], "custom spear G13 uses the authored post-native probability continuation");
    }

    private static void CheckSilverEconomyVectors()
    {
        var expectedHighQualityProbabilityTail = new[]
        {
            -225, -235, -245, -255, -265, -275, -285, -295, -305, -315, 0
        };
        var expectedHighQualityCostMultipliers = new[]
        {
            20, 25, 30, 35, 40, 45, 50, 55, 60, 65
        };
        var expectedHighGradeProbabilityTail = new[]
        {
            -245, -270, -295, -320, -345, -370, -395,
            -420, -445, -470, -495, -520, -545, 0
        };
        var expectedHighGradeCostMultipliers = new[]
        {
            25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85
        };
        var expectedEconomyUnits = new[]
        {
            1, 10, 20, 30, 40, 50, 600, 700, 800, 900,
            1000, 1050, 1100, 1150, 1200
        };
        var actualEconomyUnits = EquipmentForgeCatalog.All
            .Select(rule => rule.Bmoney)
            .Where(costs => costs.Count >= 2)
            .Select(costs => costs[1])
            .Distinct()
            .Order()
            .ToArray();

        Check.True(
            actualEconomyUnits.SequenceEqual(expectedEconomyUnits),
            "equipment-forge catalog preserves every shipped Bmoney economy tier");

        foreach (var rule in EquipmentForgeCatalog.All)
        {
            Check.True(
                rule.BaseProyAdd.Count >= EquipmentForgeCalculator.MaximumQuality,
                $"equipment {rule.ItemId} exposes quality probability through the Q20 sentinel");
            Check.True(
                rule.BaseProyAdd.Skip(9).Take(expectedHighQualityProbabilityTail.Length)
                    .SequenceEqual(expectedHighQualityProbabilityTail),
                $"equipment {rule.ItemId} Q10-Q20 quality probability tail is authoritative");
            Check.True(
                rule.Bmoney.Count >= EquipmentForgeCalculator.MaximumQuality,
                $"equipment {rule.ItemId} exposes Bmoney through the Q20 sentinel");

            var economyUnit = rule.Bmoney[1];
            for (var index = 0; index < expectedHighQualityCostMultipliers.Length; index++)
            {
                Check.Equal(
                    checked(economyUnit * expectedHighQualityCostMultipliers[index]),
                    rule.Bmoney[9 + index],
                    $"equipment {rule.ItemId} Q{10 + index} silver cost follows its economy tier");
            }
            Check.Equal(0, rule.Bmoney[19], $"equipment {rule.ItemId} Q20 cost remains a terminal sentinel");

            Check.True(
                rule.AppendProyAdd.Count >= EquipmentForgeCalculator.MaximumGrade,
                $"equipment {rule.ItemId} exposes append probability through the G25 sentinel");
            Check.True(
                rule.AppendProyAdd.Skip(11).Take(expectedHighGradeProbabilityTail.Length)
                    .SequenceEqual(expectedHighGradeProbabilityTail),
                $"equipment {rule.ItemId} G12-G25 append probability tail is authoritative");
            Check.True(
                rule.Cmoney.Count >= EquipmentForgeCalculator.MaximumGrade,
                $"equipment {rule.ItemId} exposes Cmoney through the G25 sentinel");
            for (var index = 0; index < expectedHighGradeCostMultipliers.Length; index++)
            {
                Check.Equal(
                    checked(economyUnit * expectedHighGradeCostMultipliers[index]),
                    rule.Cmoney[11 + index],
                    $"equipment {rule.ItemId} G{12 + index} silver cost follows its economy tier");
            }
            Check.Equal(0, rule.Cmoney[24], $"equipment {rule.ItemId} G25 cost remains a terminal sentinel");
        }

        Check.True(EquipmentForgeCatalog.TryGet(1435, out var q20Rule), "Q20 spear rule resolves");
        Check.True(
            q20Rule.Bmoney.Take(12).SequenceEqual(
                [960, 1200, 2160, 3360, 3480, 3600, 3720, 7440, 22320, 24000, 30000, 36000]),
            "pre-authored Q20 vector receives the authoritative native and extended silver costs");
        Check.True(
            q20Rule.Bmoney.Skip(12).SequenceEqual(
                [42000, 48000, 54000, 60000, 66000, 72000, 78000, 0]),
            "pre-authored Q20 vector receives the authoritative Q13-Q19 cost continuation and Q20 sentinel");
    }

    private static void CheckItemTemplateProgressionCoverage()
    {
        var qualityVectorNames = new[]
        {
            "Attack", "AttackRadius", "AttackSpeed", "MaxHP", "MaxMP", "Defence",
            "MagicAk", "MagicRec", "Hit", "Miss", "State", "StateImmunity",
            "AcceptCure", "Cure", "PhysicalDamage", "MagicDamage",
            "Speed", "FuryAddAk", "FuryAddRec", "InjureImbibe"
        };
        var templates = ItemTemplateSeeds.All.ToDictionary(template => template.Id);
        foreach (var rule in EquipmentForgeCatalog.All)
        {
            Check.True(
                templates.TryGetValue(checked((int)rule.ItemId), out var template),
                $"equipment-forge item {rule.ItemId} has a generated item template");

            using var stats = System.Text.Json.JsonDocument.Parse(template.StatsJson);
            CheckRequiredVectorCoverage(
                stats.RootElement,
                "BaseFraction",
                EquipmentForgeCalculator.MaximumQuality,
                rule.ItemId,
                "Q20");
            CheckRequiredVectorCoverage(
                stats.RootElement,
                "AppFraction",
                EquipmentForgeCalculator.MaximumGrade,
                rule.ItemId,
                "G25");

            foreach (var name in qualityVectorNames)
            {
                CheckOptionalVectorCoverage(
                    stats.RootElement,
                    name,
                    EquipmentForgeCalculator.MaximumQuality,
                    rule.ItemId,
                    "Q20");
            }
        }

    }
}
