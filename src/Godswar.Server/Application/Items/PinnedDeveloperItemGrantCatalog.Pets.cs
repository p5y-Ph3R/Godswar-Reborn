using System.Globalization;
using System.Text.Json;

namespace Godswar.Server.Application.Items;

internal sealed partial class PinnedDeveloperItemGrantCatalog
{
    private static readonly PetConsumableGrantSpec[] PetConsumableGrantSpecs =
    [
        new(10084, "Mysterious Tuck Net", "900,936", 0, true, 99,
            ["capturetool", "catchtool", "tucknet"], Skill: 4734),
        new(10103, "Merged Spirit", "756,972", 15, false, 99,
            ["mergedspirit", "mergingspirit", "mergespirit"]),
        new(10104, "Rebirth Spirit", "792,972", 16, false, 99,
            ["rebirthspirit"]),
        new(10105, "Contract Spirit", "828,972", 17, false, 99,
            ["contractspirit"]),
        new(10106, "Pixie Tear", "864,972", null, false, 99,
            ["pixietear"]),
        new(10107, "Spring Water", "900,972", 12, true, 99,
            ["springwater", "rebirthwater"]),
        new(10108, "Seal Jade (Empty)", "936,972", null, false, 99,
            ["emptysealjade", "sealjadeempty", "sealstone"]),
        new(10134, "Morning Dew 5", "144,756", 18, true, 99,
            ["mdew5", "morningdew5"], Skill: 4721,
            Values: 10_000_000),
        new(11015, "Pet Gender Reverser", "72,900", null, false, 1,
            ["petgenderreverser", "genderreverser"],
            "./Localization/en_us/UI/Texture/Icon.gwo")
    ];

    private static IReadOnlyList<PetConsumableDeveloperGrant>
        CreatePetConsumableGrants(IItemTemplateCatalog templates)
    {
        var values = new List<PetConsumableDeveloperGrant>(
            PetConsumableGrantSpecs.Length);
        foreach (var spec in PetConsumableGrantSpecs)
        {
            if (!templates.TryGet(spec.ItemId, out var template))
            {
                continue;
            }

            ValidatePetConsumableTemplate(template, spec);
            values.Add(new PetConsumableDeveloperGrant(
                spec,
                new DeveloperGrantMaterialDefinition(
                    spec.ItemId,
                    spec.DisplayName,
                    spec.StackCap,
                    GrantedBound: 0)));
        }

        if (values.Count is not 0 &&
            values.Count != PetConsumableGrantSpecs.Length)
        {
            throw new InvalidOperationException(
                "The reviewed pet-consumable developer family is incomplete.");
        }
        return values;
    }

    internal static bool IsPetConsumableDeveloperGrant(uint itemId) =>
        PetConsumableGrantSpecs.Any(
            value => checked((uint)value.ItemId) == itemId);

    private static Dictionary<string, DeveloperGrantMaterialDefinition>
        CreatePetConsumableAliases(
            IReadOnlyList<PetConsumableDeveloperGrant> values)
    {
        var aliases = new Dictionary<string, DeveloperGrantMaterialDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            AddAlias(aliases, value.Grant.DisplayName, value.Grant);
            AddAlias(aliases, $"pet{value.Spec.ItemId}", value.Grant);
            foreach (var alias in value.Spec.Aliases)
            {
                AddAlias(aliases, alias, value.Grant);
            }
        }
        return aliases;
    }

    private static void ValidatePetConsumableTemplate(
        ItemTemplateDefinition template,
        PetConsumableGrantSpec spec)
    {
        if (template.Id != spec.ItemId ||
            !template.Kind.Equals("consume item", StringComparison.Ordinal) ||
            !template.DisplayName.Equals(
                spec.DisplayName,
                StringComparison.Ordinal) ||
            template.EquipmentSlot != 0 ||
            template.ClassIds.Count != 0 ||
            template.MinLevel.HasValue ||
            template.MaxLevel.HasValue ||
            template.Hand.HasValue ||
            template.SkillFlag.HasValue ||
            !template.NameKey.Equals(
                $"Pet{spec.ItemId}",
                StringComparison.Ordinal) ||
            !template.Texture.Equals(
                spec.Texture ??
                    "./Localization/en_us/UI/Texture/Icon2.gwo",
                StringComparison.Ordinal) ||
            !template.Icon.Equals(spec.Icon, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pet consumable {spec.ItemId} conflicts with reviewed content.");
        }

        using var document = JsonDocument.Parse(template.StatsJson);
        var root = document.RootElement;
        if (!HasString(
                root,
                "ID",
                spec.ItemId.ToString(CultureInfo.InvariantCulture)) ||
            !HasString(root, "Type", "consume item") ||
            !HasString(root, "Texture", spec.Texture ??
                "./Localization/en_us/UI/Texture/Icon2.gwo") ||
            !HasString(root, "Icon", spec.Icon) ||
            !HasString(
                root,
                "Overlap",
                spec.StackCap.ToString(CultureInfo.InvariantCulture)) ||
            (spec.ItemType.HasValue
                ? !HasString(
                    root,
                    "ItemType",
                    spec.ItemType.Value.ToString(
                        CultureInfo.InvariantCulture))
                : root.TryGetProperty("ItemType", out _)) ||
            (spec.Use
                ? !HasString(root, "Use", "1")
                : root.TryGetProperty("Use", out _)) ||
            (spec.Skill.HasValue
                ? !HasString(
                    root,
                    "Skill",
                    spec.Skill.Value.ToString(
                        CultureInfo.InvariantCulture))
                : root.TryGetProperty("Skill", out _)) ||
            (spec.Values.HasValue
                ? !HasString(
                    root,
                    "Values",
                    spec.Values.Value.ToString(
                        CultureInfo.InvariantCulture))
                : root.TryGetProperty("Values", out _)) ||
            root.EnumerateObject().Count() !=
                8 + (spec.ItemType.HasValue ? 1 : 0) +
                (spec.Use ? 1 : 0) + (spec.Skill.HasValue ? 1 : 0) +
                (spec.Values.HasValue ? 1 : 0))
        {
            throw new InvalidOperationException(
                $"Pet consumable {spec.ItemId} has invalid stack metadata.");
        }

    }

    private sealed record PetConsumableGrantSpec(
        uint ItemId,
        string DisplayName,
        string Icon,
        int? ItemType,
        bool Use,
        short StackCap,
        IReadOnlyList<string> Aliases,
        string? Texture = null,
        int? Skill = null,
        long? Values = null);

    private sealed record PetConsumableDeveloperGrant(
        PetConsumableGrantSpec Spec,
        DeveloperGrantMaterialDefinition Grant);
}
