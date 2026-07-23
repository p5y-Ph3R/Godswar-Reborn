using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;


internal static partial class EquipmentForgeCatalogChecks
{
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
}
