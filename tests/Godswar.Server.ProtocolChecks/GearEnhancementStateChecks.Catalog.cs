using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearEnhancementStateChecks
{
    private static void CheckMaterialCatalog()
    {
        Check.Equal(58, GearEnhancementMaterialCatalog.All.Count,
            "gear-enhancement material count");
        Check.Equal(52, GearEnhancementMaterialCatalog.AttributeStones.Count,
            "Attribute Stone count");
        Check.Equal(
            GearEnhancementMaterialCatalog.All.Count,
            GearEnhancementMaterialCatalog.All.Select(
                static material => material.ItemId).Distinct().Count(),
            "gear-enhancement material IDs are unique");
        Check.True(
            !GearEnhancementMaterialCatalog.TryGet(9939, out _),
            "the ItemBaseAttribute gap at item 9939 remains unsupported");

        Check.True(
            GearEnhancementMaterialCatalog.TryGet(16300, out var prometheus) &&
            prometheus.DisplayName == "Prometheus Stone" &&
            prometheus.Texture ==
                "./Localization/en_us/UI/Texture/Icon2.gwo" &&
            prometheus.Icon == "648,180" &&
            prometheus.AllowedAttributeIds.SequenceEqual([480, 481, 482]),
            "Prometheus Stone owns one item and all three Fire families");
        Check.True(
            GearEnhancementMaterialCatalog.TryGet(
                16318,
                out var hades) &&
            hades.DisplayName == "Hades Stone" &&
            hades.Icon == "864,180" &&
            hades.AllowedAttributeIds.SequenceEqual([498, 499, 500]),
            "Hades Stone closes the canonical elemental range");
        CheckElementalIconGrid();

        Check.True(
            GearEnhancementMaterialCatalog.TryGet(9930, out var strength),
            "Strength Stone resolves");
        Check.Equal("Material1", strength.NameKey,
            "Strength Stone name key");
        Check.Equal("Strength Stone", strength.DisplayName,
            "Strength Stone display name");
        Check.Equal("./Localization/en_us/UI/Texture/Icon2.gwo",
            strength.Texture, "Strength Stone texture");
        Check.Equal("504,468", strength.Icon, "Strength Stone icon");
        Check.True(
            strength.AllowedAttributeIds.SequenceEqual([0, 1, 2, 3, 4]),
            "Strength Stone maps to the physical-attack template chain");
        Check.True(strength.CanEnhance,
            "Strength Stone is Quartz-enhanceable");

        Check.True(
            GearEnhancementMaterialCatalog.TryGet(
                9959,
                out var penetration),
            "Spirit of Penetration resolves");
        Check.True(
            penetration.AllowedAttributeIds.SequenceEqual([250]),
            "Spirit of Penetration maps to IgnoreMagPer");
        Check.True(
            GearEnhancementMaterialCatalog.TryGet(9970, out var vitality),
            "Stone of Vitality resolves");
        Check.True(
            vitality.AllowedAttributeIds.SequenceEqual(
                Enumerable.Range(300, 8)),
            "Stone of Vitality maps to the MaxHPG chain");
        Check.True(
            GearEnhancementMaterialCatalog.TryGet(9985, out var impact),
            "Stone of Impact resolves");
        Check.Equal("Material45", impact.NameKey,
            "Stone of Impact name key");
        Check.Equal("216,108", impact.Icon, "Stone of Impact icon");
        Check.True(
            impact.AllowedAttributeIds.SequenceEqual(
                Enumerable.Range(450, 8)),
            "Stone of Impact maps to the CriIncVal chain");
        Check.True(!impact.CanEnhance,
            "legendary stones are add/delete-only");

        CheckQuartzAndCatalysts();
        var template = strength.ToItemTemplateSeed();
        Check.Equal(9930, template.Id, "Strength Stone item-template ID");
        Check.Equal("consume item", template.Kind,
            "Strength Stone item-template kind");
        using var stats = JsonDocument.Parse(template.StatsJson);
        Check.Equal("99",
            stats.RootElement.GetProperty("Overlap").GetString() ??
            string.Empty,
            "Strength Stone stack cap");
        Check.Equal("50,150",
            stats.RootElement.GetProperty("Distribution").GetString() ??
            string.Empty,
            "Strength Stone distribution");
    }

    private static void CheckElementalIconGrid()
    {
        var names = new[]
        {
            "Prometheus Stone",
            "Poseidon Stone",
            "Zeus Stone",
            "Gaia Stone",
            "Aeolus Stone",
            "Apollo Stone",
            "Hades Stone"
        };
        var icons = new HashSet<string>(StringComparer.Ordinal);
        for (var element = 0; element < 7; element++)
        {
            var itemId = checked((uint)(16300 + (element * 3)));
            var expectedIcon = $"{648 + (element * 36)},180";
            Check.True(
                GearEnhancementMaterialCatalog.TryGet(
                    itemId,
                    out var stone) &&
                stone.DisplayName == names[element] &&
                stone.Icon == expectedIcon &&
                stone.AllowedAttributeIds.SequenceEqual(
                    Enumerable.Range(480 + (element * 3), 3)),
                $"canonical elemental stone {itemId} uses icon {expectedIcon}");
            icons.Add(stone.Icon);
        }
        Check.Equal(7, icons.Count,
            "all canonical elemental stones use distinct Icon2 cells");

        foreach (var retiredItemId in new uint[]
                 {
                     16301, 16302, 16304, 16305, 16307, 16308,
                     16310, 16311, 16313, 16314, 16316, 16317,
                     16319, 16320
                 })
        {
            Check.True(
                !GearEnhancementMaterialCatalog.TryGet(
                    retiredItemId,
                    out _),
                $"retired elemental item {retiredItemId} has no active material policy");
        }
    }

    private static void CheckQuartzAndCatalysts()
    {
        for (var level = 1; level <= 4; level++)
        {
            Check.True(
                GearEnhancementMaterialCatalog.TryGet(
                    checked((uint)(9959 + level)),
                    out var quartz),
                $"Quartz Plate {level} resolves");
            Check.Equal((short)level,
                quartz.SourceAttributeLevel ?? 0,
                $"Quartz Plate {level} source level");
            Check.Equal((short)(level + 1),
                quartz.TargetAttributeLevel ?? 0,
                $"Quartz Plate {level} target level");
        }

        Check.True(
            GearEnhancementMaterialCatalog.TryGet(
                GearEnhancementMaterialCatalog.FlameSparkItemId,
                out var flame) &&
            flame.Kind == GearEnhancementMaterialKind.FlameSpark,
            "Flame Spark catalyst resolves");
        Check.True(
            GearEnhancementMaterialCatalog.TryGet(
                GearEnhancementMaterialCatalog.WaterGrainItemId,
                out var water) &&
            water.Kind == GearEnhancementMaterialKind.WaterGrain,
            "Water Grain catalyst resolves");
    }
}
