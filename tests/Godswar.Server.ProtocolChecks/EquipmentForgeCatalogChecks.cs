using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class EquipmentForgeCatalogChecks
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

    private static void CheckClassNeutralEquipmentRankProfiles()
    {
        const int specialGmSpearId = 1499;
        const int specialGmArmorId = 2190;

        int[] weaponBaseFraction =
        [
            0, 8, 18, 28, 40, 54, 74, 100, 140, 200,
            260, 340, 440, 560, 700, 860, 1040, 1240, 1460, 1700
        ];
        int[] weaponAppFraction =
        [
            10, 13, 16, 20, 24, 28, 32, 40, 50, 60, 80, 100,
            130, 170, 220, 280, 350, 430, 520, 620, 730, 850, 980, 1120, 1270
        ];
        int[] weaponRankThresholds =
        [
            40, 100, 180, 240, 300, 460, 600, 1200, 4000, 8000,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1
        ];
        int[] physicalWeaponRankEffects =
        [
            1, 2, 3, 4, 5, 5, 5, 6, 8, 9,
            5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5
        ];
        int[] priestWeaponRankEffects =
        [
            201, 202, 203, 204, 205, 205, 205, 206, 208, 209,
            205, 205, 205, 205, 205, 205, 205, 205, 205, 205, 205, 205, 205, 205, 205
        ];
        int[] mageWeaponRankEffects =
        [
            51, 52, 53, 54, 55, 55, 55, 56, 58, 59,
            55, 55, 55, 55, 55, 55, 55, 55, 55, 55, 55, 55, 55, 55, 55
        ];
        int[] armorRankThresholds =
        [330, 475, 750, 950, 1350, 1720, 2225, 3860, 5250, 8000, 12000, 17000, 22000, 25300, -1];
        int[] armorRankEffects = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 14];

        int[] bodyBasePrefix = [0, 12, 27, 42, 60, 81, 111, 150, 210, 300];
        int[] bodyAppPrefix = [15, 19, 24, 30, 36, 42, 48, 60, 75, 90, 120, 150];
        int[] mediumBasePrefix = [0, 8, 18, 28, 40, 54, 74, 100, 140, 200];
        int[] mediumAppPrefix = [10, 13, 16, 20, 24, 28, 32, 40, 50, 60, 80, 100];
        int[] lightBasePrefix = [0, 7, 15, 24, 34, 45, 62, 85, 119, 170];
        int[] lightAppPrefix = [8, 10, 14, 17, 20, 24, 27, 34, 42, 51, 68, 85];
        int[] shieldBasePrefix = [0, 2, 4, 7, 10, 13, 18, 25, 35, 50];
        int[] shieldAppPrefix = [2, 3, 4, 5, 6, 7, 8, 10, 12, 15, 20, 25];

        var templates = ItemTemplateSeeds.All.ToDictionary(template => template.Id);
        foreach (var rule in EquipmentForgeCatalog.All)
        {
            var itemId = checked((int)rule.ItemId);
            Check.True(templates.TryGetValue(itemId, out var template), $"rank-profile item {itemId} resolves");

            using var stats = System.Text.Json.JsonDocument.Parse(template.StatsJson);
            var baseFraction = ReadIntegerVector(stats.RootElement, "BaseFraction", itemId);
            var appFraction = ReadIntegerVector(stats.RootElement, "AppFraction", itemId);

            if (itemId == specialGmArmorId)
            {
                continue;
            }

            if (template.Kind == "weapon")
            {
                if (itemId == specialGmSpearId)
                {
                    continue;
                }

                Check.True(
                    baseFraction.SequenceEqual(weaponBaseFraction),
                    $"ordinary weapon {itemId} uses the canonical Q20 rank-score profile");
                Check.True(
                    appFraction.SequenceEqual(weaponAppFraction),
                    $"ordinary weapon {itemId} uses the canonical G25 rank-score profile");
                var fiveAttributeScore = baseFraction[19] + (5 * appFraction[24]);
                var fourAttributeScore = baseFraction[19] + (4 * appFraction[24]);
                Check.Equal(8050, fiveAttributeScore, $"ordinary weapon {itemId} five-attribute maximum score");
                Check.Equal(6780, fourAttributeScore, $"ordinary weapon {itemId} four-attribute maximum score");

                var rankThresholds = ReadIntegerVector(stats.RootElement, "ArmEffFraction", itemId);
                Check.True(
                    rankThresholds.SequenceEqual(weaponRankThresholds),
                    $"ordinary weapon {itemId} exposes the canonical WR10 threshold table");
                Check.Equal(
                    10,
                    rankThresholds.Count(threshold => threshold > 0 && threshold <= fiveAttributeScore),
                    $"ordinary weapon {itemId} reaches WR10 with five attributes");
                Check.Equal(
                    9,
                    rankThresholds.Count(threshold => threshold > 0 && threshold <= fourAttributeScore),
                    $"ordinary weapon {itemId} reaches WR9 with four attributes");

                Check.Equal(1, template.ClassIds.Length, $"ordinary weapon {itemId} has one authoritative profession");
                var expectedEffects = template.ClassIds[0] switch
                {
                    0 or 1 => physicalWeaponRankEffects,
                    2 => priestWeaponRankEffects,
                    3 => mageWeaponRankEffects,
                    _ => throw new InvalidOperationException(
                        $"Ordinary weapon {itemId} has unsupported profession {template.ClassIds[0]}.")
                };
                var rankEffects = ReadIntegerVector(stats.RootElement, "ArmEff", itemId);
                Check.True(
                    rankEffects.SequenceEqual(expectedEffects),
                    $"ordinary weapon {itemId} uses its profession-specific rank-effect family");
                continue;
            }

            (int[] Base, int[] App) expectedPrefix = template.Kind switch
            {
                "armor" or "cloth" => (bodyBasePrefix, bodyAppPrefix),
                "head" or "glove" or "girdle" or "shoes" => (mediumBasePrefix, mediumAppPrefix),
                "amulet" or "cuff" or "leggins" or "ring" => (lightBasePrefix, lightAppPrefix),
                "shield" => (shieldBasePrefix, shieldAppPrefix),
                _ => throw new InvalidOperationException(
                    $"Forgeable nonweapon {itemId} has unsupported kind '{template.Kind}'.")
            };

            Check.True(
                baseFraction.Take(10).SequenceEqual(expectedPrefix.Base),
                $"forgeable {template.Kind} {itemId} preserves its native Q1-Q10 score prefix");
            Check.True(
                appFraction.Take(12).SequenceEqual(expectedPrefix.App),
                $"forgeable {template.Kind} {itemId} preserves its native G1-G12 score prefix");
            Check.Equal(
                checked(baseFraction[9] * 3),
                baseFraction[19],
                $"forgeable {template.Kind} {itemId} ends Q20 at three times Q10");
            Check.Equal(
                checked(appFraction[11] * 4),
                appFraction[24],
                $"forgeable {template.Kind} {itemId} ends G25 at four times G12");

            if (template.Kind is "armor" or "cloth")
            {
                Check.True(
                    ReadIntegerVector(stats.RootElement, "DefendFraction", itemId).SequenceEqual(armorRankThresholds),
                    $"forgeable body armor {itemId} exposes the canonical AR14 threshold table");
                Check.True(
                    ReadIntegerVector(stats.RootElement, "DefendEff", itemId).SequenceEqual(armorRankEffects),
                    $"forgeable body armor {itemId} exposes the canonical AR14 effect table");
            }
        }

        using (var specialStats = System.Text.Json.JsonDocument.Parse(templates[specialGmSpearId].StatsJson))
        {
            CheckVectorEquals(
                specialStats.RootElement,
                "BaseFraction",
                "0,8,18,28,40,54,74,100,140,200,260,340,440,540,640,740,840,940,1040,1140",
                specialGmSpearId,
                "special GM Spear quality score remains byte-for-byte unchanged");
            CheckVectorEquals(
                specialStats.RootElement,
                "AppFraction",
                "10,13,16,20,24,28,32,40,50,60,80,100,100,100,100,100,100,100,100,100,100,100,100,100,100",
                specialGmSpearId,
                "special GM Spear grade score remains byte-for-byte unchanged");
            CheckVectorEquals(
                specialStats.RootElement,
                "MainAttribute",
                "0,40,60,80,130,150,180,180,180,180,180,180,180",
                specialGmSpearId,
                "special GM Spear allowed-main-attribute list remains byte-for-byte unchanged");
            CheckVectorEquals(
                specialStats.RootElement,
                "ArmEffFraction",
                "40,100,180,240,300,460,600,720,780,820,820,820,820",
                specialGmSpearId,
                "special GM Spear rank thresholds remain byte-for-byte unchanged");
            CheckVectorEquals(
                specialStats.RootElement,
                "ArmEff",
                "1,2,3,4,5,5,5,6,7,8,9,9,9",
                specialGmSpearId,
                "special GM Spear rank effects remain byte-for-byte unchanged");
        }

        using (var specialStats = System.Text.Json.JsonDocument.Parse(templates[specialGmArmorId].StatsJson))
        {
            CheckVectorEquals(
                specialStats.RootElement,
                "BaseFraction",
                "0,12,27,42,60,81,111,150,210,300,345,390,443,496,549,602,655,708,761,814",
                specialGmArmorId,
                "special GM Armor quality score remains byte-for-byte unchanged");
            CheckVectorEquals(
                specialStats.RootElement,
                "AppFraction",
                "15,19,24,30,36,42,48,60,75,90,120,150,180,210,240,270,300,330,360,390,420,450,480,510,540",
                specialGmArmorId,
                "special GM Armor grade score remains byte-for-byte unchanged");
            CheckVectorEquals(
                specialStats.RootElement,
                "MainAttribute",
                "10,30,50,100,120,130,140,150,160,170,170,170,170",
                specialGmArmorId,
                "special GM Armor allowed-main-attribute list remains byte-for-byte unchanged");
            CheckVectorEquals(
                specialStats.RootElement,
                "DefendFraction",
                "330,475,750,950,1350,1720,2225,3860,5250,5250,5250,5250,5250",
                specialGmArmorId,
                "special GM Armor rank thresholds remain byte-for-byte unchanged");
            CheckVectorEquals(
                specialStats.RootElement,
                "DefendEff",
                "1,2,3,4,5,6,7,8,9,9,9,9,9",
                specialGmArmorId,
                "special GM Armor rank effects remain byte-for-byte unchanged");
        }

        const int bodyMaximumScore = 900 + (5 * 600);
        const int mediumMaximumScore = 600 + (5 * 400);
        const int lightMaximumScore = 510 + (5 * 340);
        const int shieldMaximumScore = 150 + (5 * 100);
        var noShieldSetScore =
            mediumMaximumScore + // head
            lightMaximumScore + // amulet
            mediumMaximumScore + // gloves
            bodyMaximumScore +
            lightMaximumScore + // sleeves
            mediumMaximumScore + // girdle
            mediumMaximumScore + // boots
            lightMaximumScore + // leggings
            (2 * lightMaximumScore); // rings

        Check.Equal(25350, noShieldSetScore, "complete five-attribute no-shield set reaches AR14 exactly");
        Check.Equal(650, shieldMaximumScore, "Warrior/Priest shield profile contributes 650 rank score");
        Check.Equal(26000, noShieldSetScore + shieldMaximumScore, "complete Warrior/Priest set remains at the intended score");
        Check.True(
            noShieldSetScore + shieldMaximumScore < short.MaxValue,
            "complete Warrior/Priest set remains below the signed-short client score boundary");
    }

    private static int[] ReadIntegerVector(
        System.Text.Json.JsonElement stats,
        string name,
        int itemId)
    {
        Check.True(stats.TryGetProperty(name, out var element), $"item {itemId} has {name}");
        return (element.GetString() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();
    }

    private static void CheckVectorEquals(
        System.Text.Json.JsonElement stats,
        string name,
        string expected,
        int itemId,
        string description)
    {
        Check.True(stats.TryGetProperty(name, out var element), $"item {itemId} has {name}");
        Check.Equal(expected, element.GetString() ?? string.Empty, description);
    }

    private static void CheckRequiredVectorCoverage(
        System.Text.Json.JsonElement stats,
        string name,
        int minimumCount,
        uint itemId,
        string ceiling)
    {
        Check.True(
            stats.TryGetProperty(name, out var element),
            $"equipment-forge item {itemId} has {name} stats");
        CheckVectorCoverage(element, name, minimumCount, itemId, ceiling);
    }

    private static void CheckOptionalVectorCoverage(
        System.Text.Json.JsonElement stats,
        string name,
        int minimumCount,
        uint itemId,
        string ceiling)
    {
        if (stats.TryGetProperty(name, out var element))
        {
            CheckVectorCoverage(element, name, minimumCount, itemId, ceiling);
        }
    }

    private static void CheckVectorCoverage(
        System.Text.Json.JsonElement element,
        string name,
        int minimumCount,
        uint itemId,
        string ceiling)
    {
        var values = element.GetString() ?? string.Empty;
        Check.True(
            values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length >=
            minimumCount,
            $"equipment-forge item {itemId} exposes generated {name} through {ceiling}");
    }

    private static void CheckMaterialRules()
    {
        Check.Equal(
            ForgingMaterialRuleCatalog.ShippedRuleCount,
            ForgingMaterialRuleCatalog.All.Count,
            "shipped BijouForge rule count");
        Check.True(ForgingMaterialRuleCatalog.TryGet(4210, out var sapphire), "Level 1 Sapphire rule resolves");
        Check.Equal(2, sapphire.MaterialType, "Sapphire material type");
        Check.True(!sapphire.MaterialProyAdd.HasValue, "missing Sapphire bonus remains nullable");
        Check.True(sapphire.AllowsRound(0), "Sapphire endpoint range includes round zero");
        Check.True(sapphire.AllowsRound(5), "Sapphire endpoint range includes an interior round");
        Check.True(sapphire.AllowsRound(7), "Sapphire endpoint range includes round seven");
        Check.True(!sapphire.AllowsRound(8), "Sapphire endpoint range excludes round eight");

        Check.True(ForgingMaterialRuleCatalog.TryGet(4213, out var highSapphire), "Level 4 Sapphire rule resolves");
        Check.True(highSapphire.Round.SequenceEqual([8, 12]), "Level 4 Sapphire native endpoint range is preserved");
        Check.True(!highSapphire.AllowsRound(7), "Level 4 Sapphire excludes round seven");
        Check.True(highSapphire.AllowsRound(8), "Level 4 Sapphire includes its lower endpoint");
        Check.True(highSapphire.AllowsRound(10), "Level 4 Sapphire includes Mystic quality");
        Check.True(highSapphire.AllowsRound(12), "Level 4 Sapphire includes its upper endpoint");
        Check.True(!highSapphire.AllowsRound(13), "Level 4 Sapphire excludes the Q13 ceiling");
        Check.True(!ForgingMaterialRuleCatalog.TryGet(4214, out _), "Sapphire pieces are not forgeable materials");

        Check.True(ForgingMaterialRuleCatalog.TryGet(4215, out var sapphireFive), "Level 5 Sapphire rule resolves");
        Check.Equal(2, sapphireFive.MaterialType, "Level 5 Sapphire material type");
        Check.Equal(32, sapphireFive.ProbabilityBonus, "Level 5 Sapphire probability bonus");
        Check.True(sapphireFive.Round.SequenceEqual([8, 19]), "Level 5 Sapphire reaches the Q19-to-Q20 attempt");
        Check.True(sapphireFive.AllowsRound(8), "Level 5 Sapphire includes its overlap-band lower endpoint");
        Check.True(sapphireFive.AllowsRound(19), "Level 5 Sapphire includes the final Q19 input");
        Check.True(!sapphireFive.AllowsRound(20), "Level 5 Sapphire excludes the Q20 ceiling");

        Check.True(ForgingMaterialRuleCatalog.TryGet(4223, out var emeraldFour), "Level 4 Emerald rule resolves");
        Check.Equal(24, emeraldFour.ProbabilityBonus, "Level 4 Emerald probability bonus");
        Check.True(emeraldFour.Round.SequenceEqual([10, 17]), "Level 4 Emerald reaches the G17-to-G18 attempt");
        Check.True(emeraldFour.AllowsRound(10), "Level 4 Emerald includes its lower endpoint");
        Check.True(emeraldFour.AllowsRound(17), "Level 4 Emerald includes its upper endpoint");
        Check.True(!emeraldFour.AllowsRound(18), "Level 4 Emerald excludes the G18 ceiling");
        Check.True(!ForgingMaterialRuleCatalog.TryGet(4224, out _), "Emerald pieces are not forgeable materials");

        Check.True(ForgingMaterialRuleCatalog.TryGet(4225, out var emeraldFive), "Level 5 Emerald rule resolves");
        Check.Equal(3, emeraldFive.MaterialType, "Level 5 Emerald material type");
        Check.Equal(32, emeraldFive.ProbabilityBonus, "Level 5 Emerald probability bonus");
        Check.True(emeraldFive.Round.SequenceEqual([10, 24]), "Level 5 Emerald reaches the G24-to-G25 attempt");
        Check.True(emeraldFive.AllowsRound(10), "Level 5 Emerald includes its overlap-band lower endpoint");
        Check.True(emeraldFive.AllowsRound(24), "Level 5 Emerald includes the final G24 input");
        Check.True(!emeraldFive.AllowsRound(25), "Level 5 Emerald excludes the G25 ceiling");

        Check.True(ForgingMaterialRuleCatalog.TryGet(4234, out var crystalFive), "Level 5 Crystal rule resolves");
        Check.Equal(4, crystalFive.MaterialType, "Level 5 Crystal material type");
        Check.Equal(25, crystalFive.ProbabilityBonus, "Level 5 Crystal probability bonus per selected crystal");
        Check.Equal(0, crystalFive.Round.Count, "Level 5 Crystal has no quality/grade round restriction");
    }

    private static void CheckRubyCalculation()
    {
        var request = new EquipmentForgeRequest(
            Item(1001, quality: 4, grade: 6),
            new EquipmentForgeMaterialSelection(Item(4200), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 5), 5));

        Check.True(
            EquipmentForgeCalculator.TryCalculate(request, out var calculation, out var error),
            $"Ruby calculation succeeds ({error})");
        Check.Equal((int)EquipmentForgeOperation.Ruby, (int)calculation!.Operation, "Ruby operation");
        Check.Equal(100, calculation.SuccessProbability, "Ruby probability clamps to 100");
        Check.Equal(177, calculation.SilverCost, "Ruby uses Amoney");
        Check.Equal(1002u, calculation.SuccessEquipment.Id, "Ruby success uses NextID");
        Check.Equal(
            request.Equipment with { Id = 1002 },
            calculation.SuccessEquipment,
            "Ruby success preserves every non-template equipment field");
        Check.Equal(1001u, calculation.FailureEquipment.Id, "ordinary Ruby failure preserves equipment ID");
        Check.Equal((short)4, calculation.SuccessEquipment.Quality, "Ruby preserves quality");
        Check.Equal((short)6, calculation.SuccessEquipment.Grade, "Ruby preserves grade");

        var cappedAxesRequest = request with
        {
            Equipment = Item(
                1001,
                quality: EquipmentForgeCalculator.MaximumQuality,
                grade: EquipmentForgeCalculator.MaximumGrade),
            OddsMaterial = null
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(cappedAxesRequest, out calculation, out error),
            $"Ruby remains valid on Q20/G25 equipment ({error})");
        Check.Equal(EquipmentForgeCalculator.MaximumQuality, calculation!.SuccessEquipment.Quality, "Ruby preserves Boundless Q20");
        Check.Equal(EquipmentForgeCalculator.MaximumGrade, calculation.SuccessEquipment.Grade, "Ruby preserves G25");
    }

    private static void CheckSapphireCalculation()
    {
        var request = new EquipmentForgeRequest(
            Item(1000, quality: 1),
            new EquipmentForgeMaterialSelection(Item(4210), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 25), 25));

        Check.True(
            EquipmentForgeCalculator.TryCalculate(request, out var calculation, out var error),
            $"Sapphire calculation succeeds ({error})");
        Check.Equal((int)EquipmentForgeOperation.Sapphire, (int)calculation!.Operation, "Sapphire operation");
        Check.Equal(100, calculation.SuccessProbability, "Sapphire tutorial recipe reaches 100 percent");
        Check.Equal(1, calculation.SilverCost, "Sapphire uses Bmoney at quality minus one");
        Check.Equal((short)2, calculation.SuccessEquipment.Quality, "Sapphire success increments quality");
        Check.Equal((short)1, calculation.FailureEquipment.Quality, "Sapphire failure preserves quality");

        var superiorToClassic = new EquipmentForgeRequest(
            Item(1000, quality: 5),
            new EquipmentForgeMaterialSelection(Item(4212), 1),
            new EquipmentForgeMaterialSelection(Item(4232, stack: 17), 17));
        Check.True(
            EquipmentForgeCalculator.TryCalculate(superiorToClassic, out calculation, out error),
            $"Superior-to-Classic recipe succeeds ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "seventeen Level 3 Crystals make Q5 recipe authoritative 100 percent");
        Check.Equal(3, calculation.SilverCost, "Q5-to-Q6 recipe costs native three silver");
        Check.Equal((short)6, calculation.SuccessEquipment.Quality, "Superior upgrades to Classic quality");

        var lowSapphireBoundary = request with
        {
            Equipment = Item(1000, quality: 7),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4212), 1),
            OddsMaterial = null
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(lowSapphireBoundary, out _, out error),
            $"low Sapphire remains valid at current Q7 ({error})");

        lowSapphireBoundary = lowSapphireBoundary with { Equipment = Item(1000, quality: 8) };
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(lowSapphireBoundary, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "low Sapphire stops at current Q8");
        Check.True(
            EquipmentForgeCalculator.TryCalculate(
                lowSapphireBoundary with
                {
                    PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4213), 1)
                },
                out _,
                out error),
            $"high Sapphire starts at current Q8 ({error})");

        var lowChance = request with
        {
            Equipment = Item(1000, quality: 9),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4213), 1),
            OddsMaterial = null
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(lowChance, out calculation, out error),
            $"high-round Sapphire calculation succeeds ({error})");
        Check.Equal(0, calculation!.SuccessProbability, "negative Sapphire probability clamps to zero");
        Check.Equal(18, calculation.SilverCost, "Sapphire Q9 cost uses index eight");

        var mysticToDivine = request with
        {
            Equipment = Item(1000, quality: 10),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4213), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4233, stack: 25), 25)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(mysticToDivine, out calculation, out error),
            $"Mystic-to-Divine recipe succeeds ({error})");
        Check.Equal(99, calculation!.SuccessProbability, "Q10 recipe follows extended native probability pattern");
        Check.Equal(20, calculation.SilverCost, "Q10 recipe replaces the terminal zero-cost sentinel");
        Check.Equal((short)11, calculation.SuccessEquipment.Quality, "Mystic upgrades to Divine quality");

        var divineToCelestial = mysticToDivine with { Equipment = Item(1000, quality: 11) };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(divineToCelestial, out calculation, out error),
            $"Divine-to-Celestial recipe succeeds ({error})");
        Check.Equal(89, calculation!.SuccessProbability, "Q11 recipe follows extended native probability pattern");
        Check.Equal(25, calculation.SilverCost, "Q11 recipe uses the extended economy cost");
        Check.Equal((short)12, calculation.SuccessEquipment.Quality, "Divine upgrades to Celestial quality");

        var celestialToMythical = mysticToDivine with { Equipment = Item(1000, quality: 12) };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(celestialToMythical, out calculation, out error),
            $"Celestial-to-Mythical recipe succeeds ({error})");
        Check.Equal(79, calculation!.SuccessProbability, "Q12 recipe follows extended native probability pattern");
        Check.Equal(30, calculation.SilverCost, "Q12 recipe uses the extended economy cost");
        Check.Equal((short)13, calculation.SuccessEquipment.Quality, "Celestial upgrades to Mythical quality");

        var levelFiveCelestialToMythical = celestialToMythical with
        {
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4215), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 25), 25)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(levelFiveCelestialToMythical, out calculation, out error),
            $"Level-5 Sapphire remains valid in the Q12 overlap band ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "25 Level-5 Crystals guarantee the Q12 overlap-band attempt");

        var levelFourAtMythical = celestialToMythical with
        {
            Equipment = Item(1000, quality: 13),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4213), 1),
            OddsMaterial = null
        };
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(levelFourAtMythical, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "Level 4 Sapphire stops when the current equipment reaches Q13");

        var primordialToBoundless = celestialToMythical with
        {
            Equipment = Item(1000, quality: 19, grade: EquipmentForgeCalculator.MaximumGrade),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4215), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 25), 25)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(primordialToBoundless, out calculation, out error),
            $"Level 5 Sapphire reaches the Q19-to-Q20 boundary ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "25 Level-5 Crystals guarantee the Q19 maximum-quality attempt");
        Check.Equal(65, calculation.SilverCost, "Q19 attempt uses the authoritative economy endpoint");
        Check.Equal(EquipmentForgeCalculator.MaximumQuality, calculation.SuccessEquipment.Quality, "Q19 equipment upgrades to Boundless Q20");
        Check.Equal(EquipmentForgeCalculator.MaximumGrade, calculation.SuccessEquipment.Grade, "Sapphire preserves the cross-axis G25 ceiling");
    }

    private static void CheckEmeraldCalculation()
    {
        var request = new EquipmentForgeRequest(
            Item(1000, grade: 1) with { Attribute1 = 24 },
            new EquipmentForgeMaterialSelection(Item(4220), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 20), 20));

        Check.True(
            EquipmentForgeCalculator.TryCalculate(request, out var calculation, out var error),
            $"Emerald calculation succeeds ({error})");
        Check.Equal((int)EquipmentForgeOperation.Emerald, (int)calculation!.Operation, "Emerald operation");
        Check.Equal(100, calculation.SuccessProbability, "Emerald tutorial recipe reaches 100 percent");
        Check.Equal(0, calculation.SilverCost, "Emerald uses Cmoney at grade minus one");
        Check.Equal((short)2, calculation.SuccessEquipment.Grade, "Emerald success increments grade");
        Check.Equal((short)1, calculation.FailureEquipment.Grade, "Emerald failure preserves grade");

        var lowEmeraldBoundary = request with
        {
            Equipment = Item(1000, grade: 9) with { Attribute1 = 24 },
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4222), 1),
            OddsMaterial = null
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(lowEmeraldBoundary, out _, out error),
            $"low Emerald remains valid at current grade 9 ({error})");
        lowEmeraldBoundary = lowEmeraldBoundary with
        {
            Equipment = Item(1000, grade: 10) with { Attribute1 = 24 }
        };
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(lowEmeraldBoundary, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "low Emerald stops at current grade 10");
        Check.True(
            EquipmentForgeCalculator.TryCalculate(
                lowEmeraldBoundary with
                {
                    PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4223), 1)
                },
                out _,
                out error),
            $"high Emerald starts at current grade 10 ({error})");

        var celestialToGradeThirteen = request with
        {
            Equipment = Item(1000, grade: 12) with { Attribute1 = 24 },
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4223), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 25), 25)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(celestialToGradeThirteen, out calculation, out error),
            $"Level 4 Emerald remains valid above the former G12 ceiling ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "G12 attempt clamps the level-5 Crystal-assisted chance");
        Check.Equal(25, calculation.SilverCost, "G12 attempt begins the authored high-grade economy band");
        Check.Equal((short)13, calculation.SuccessEquipment.Grade, "G12 equipment upgrades to G13");

        var levelFourAtGradeEighteen = celestialToGradeThirteen with
        {
            Equipment = Item(1000, grade: 18) with { Attribute1 = 24 },
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4223), 1),
            OddsMaterial = null
        };
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(levelFourAtGradeEighteen, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "Level 4 Emerald stops when the current equipment reaches G18");

        var gradeTwentyFourToTwentyFive = celestialToGradeThirteen with
        {
            Equipment = Item(1000, quality: EquipmentForgeCalculator.MaximumQuality, grade: 24) with { Attribute1 = 24 },
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4225), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 25), 25)
        };
        var gradeTwentyFourWithTwentyFourCrystals = gradeTwentyFourToTwentyFive with
        {
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 24), 24)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(gradeTwentyFourWithTwentyFourCrystals, out calculation, out error),
            $"Level-5 G24 boundary remains calculable with 24 Crystals ({error})");
        Check.Equal(87, calculation!.SuccessProbability, "24 Level-5 Crystals remain below certainty at the maximum grade");

        Check.True(
            EquipmentForgeCalculator.TryCalculate(gradeTwentyFourToTwentyFive, out calculation, out error),
            $"Level 5 Emerald reaches the G24-to-G25 boundary ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "25 Level-5 Crystals guarantee the maximum-grade attempt");
        Check.Equal(85, calculation.SilverCost, "G24 attempt uses the authored high-grade economy endpoint");
        Check.Equal(EquipmentForgeCalculator.MaximumGrade, calculation.SuccessEquipment.Grade, "G24 equipment upgrades to the G25 ceiling");
        Check.Equal(EquipmentForgeCalculator.MaximumQuality, calculation.SuccessEquipment.Quality, "Emerald preserves the cross-axis Boundless Q20 ceiling");
    }

    private static void CheckValidation()
    {
        var stackedEquipment = new EquipmentForgeRequest(
            Item(1000, stack: 2),
            new EquipmentForgeMaterialSelection(Item(4200), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(stackedEquipment, out _, out var error) &&
            error == EquipmentForgeValidationError.EquipmentStackMustBeOne,
            "stacked equipment cannot multiply one forge payment across multiple items");

        var invalidQuantity = new EquipmentForgeRequest(
            Item(1000),
            new EquipmentForgeMaterialSelection(Item(4200, stack: 2), 2),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(invalidQuantity, out _, out error) &&
            error == EquipmentForgeValidationError.PrimaryQuantityMustBeOne,
            "primary material quantity must be one");

        var wrongRound = new EquipmentForgeRequest(
            Item(1000, quality: 9),
            new EquipmentForgeMaterialSelection(Item(4210), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(wrongRound, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "material Round restricts quality progression");

        var tooManyOdds = new EquipmentForgeRequest(
            Item(1000),
            new EquipmentForgeMaterialSelection(Item(4200), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 26), 26));
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(tooManyOdds, out _, out error) &&
            error == EquipmentForgeValidationError.OddsQuantityInvalid,
            "odds-crystal quantity is capped at 25");

        var noAppendAttribute = new EquipmentForgeRequest(
            Item(1000),
            new EquipmentForgeMaterialSelection(Item(4220), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(noAppendAttribute, out _, out error) &&
            error == EquipmentForgeValidationError.EmeraldRequiresAppendAttribute,
            "Emerald forging requires an append attribute");

        var zeroIdAppendAttribute = new EquipmentForgeRequest(
            Item(1000) with { Attribute1 = 0 },
            new EquipmentForgeMaterialSelection(Item(4220), 1),
            null);
        Check.True(
            EquipmentForgeCalculator.TryCalculate(zeroIdAppendAttribute, out _, out error),
            $"append attribute ID zero remains Emerald-forgeable ({error})");

        var qualityCap = new EquipmentForgeRequest(
            Item(1000, quality: EquipmentForgeCalculator.MaximumQuality),
            new EquipmentForgeMaterialSelection(Item(4215), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(qualityCap, out _, out error) &&
            error == EquipmentForgeValidationError.ProgressionOutOfRange,
            "Sapphire forging stops at the Boundless Q20 quality cap");

        var gradeCap = new EquipmentForgeRequest(
            Item(1000, grade: EquipmentForgeCalculator.MaximumGrade) with { Attribute1 = 24 },
            new EquipmentForgeMaterialSelection(Item(4225), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(gradeCap, out _, out error) &&
            error == EquipmentForgeValidationError.ProgressionOutOfRange,
            "Emerald forging stops at the G25 grade cap");

        var piece = new EquipmentForgeRequest(
            Item(1000),
            new EquipmentForgeMaterialSelection(Item(4214), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(piece, out _, out error) &&
            error == EquipmentForgeValidationError.PrimaryMaterialRuleNotFound,
            "material pieces cannot be used directly");

        var terminalRuby = new EquipmentForgeRequest(
            Item(1013),
            new EquipmentForgeMaterialSelection(Item(4202), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(terminalRuby, out _, out error) &&
            error == EquipmentForgeValidationError.MissingProbability,
            "terminal Ruby rule is rejected without inventing a probability");
    }

    private static CompactItemEntry Item(
        uint itemId,
        short quality = 1,
        short grade = 1,
        short stack = 1)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = quality,
            Grade = grade,
            Stack = stack
        };
    }
}
