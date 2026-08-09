using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Godswar.Server.Application.Items;

/// <summary>
/// The deliberately narrow developer-grant allowlist for one immutable item
/// content revision. Existing material grants are delegated to their native
/// catalog; empty Holy Boxes and the reviewed permanent costume are derived
/// from the same published item revision used by gameplay.
/// </summary>
internal sealed class PinnedDeveloperItemGrantCatalog :
    IDeveloperItemGrantCatalog
{
    internal const uint PermanentChristmasCostumeItemId = 8068;

    private readonly IItemMaterialCatalog _materials;
    private readonly FrozenDictionary<uint, DeveloperGrantMaterialDefinition>
        _holyBoxesById;
    private readonly FrozenDictionary<string, DeveloperGrantMaterialDefinition>
        _holyBoxesByAlias;
    private readonly FrozenDictionary<uint, DeveloperGrantMaterialDefinition>
        _socketSpellsById;
    private readonly FrozenDictionary<string, DeveloperGrantMaterialDefinition>
        _socketSpellsByAlias;
    private readonly FrozenDictionary<uint, DeveloperGrantMaterialDefinition>
        _costumesById;
    private readonly FrozenDictionary<string, DeveloperGrantMaterialDefinition>
        _costumesByAlias;

    public PinnedDeveloperItemGrantCatalog(IItemTemplateCatalog templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _materials = templates.Materials;

        var holyBoxes = templates.HolySuit.Consumables
            .Where(static value =>
                value.Role == HolySuitConsumableRole.HolyBox)
            .Select(value => CreateHolyBoxGrant(templates, value))
            .OrderBy(static value => value.Ordinal)
            .ToArray();
        _holyBoxesById = holyBoxes.ToFrozenDictionary(
            static value => value.Grant.ItemId,
            static value => value.Grant);
        _holyBoxesByAlias = CreateHolyBoxAliases(holyBoxes)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        var socketSpells = CreateSocketSpellGrants(templates);
        _socketSpellsById = socketSpells.ToFrozenDictionary(
            static value => value.ItemId,
            static value => value.Grant);
        _socketSpellsByAlias = CreateSocketSpellAliases(socketSpells)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        var costumes = CreateCostumeGrants(templates);
        _costumesById = costumes.ToFrozenDictionary(
            static value => value.ItemId,
            static value => value.Grant);
        _costumesByAlias = CreateCostumeAliases(costumes)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryResolveDeveloper(
        uint itemId,
        out DeveloperGrantMaterialDefinition item) =>
        _materials.TryResolveDeveloper(itemId, out item!) ||
        _holyBoxesById.TryGetValue(itemId, out item!) ||
        _socketSpellsById.TryGetValue(itemId, out item!) ||
        _costumesById.TryGetValue(itemId, out item!);

    public bool TryResolveDeveloper(
        string alias,
        out DeveloperGrantMaterialDefinition item) =>
        _materials.TryResolveDeveloper(alias, out item!) ||
        _holyBoxesByAlias.TryGetValue(NormalizeAlias(alias), out item!) ||
        _socketSpellsByAlias.TryGetValue(NormalizeAlias(alias), out item!) ||
        _costumesByAlias.TryGetValue(NormalizeAlias(alias), out item!);

    private static IReadOnlyList<CostumeDeveloperGrant>
        CreateCostumeGrants(IItemTemplateCatalog templates)
    {
        if (!templates.TryGet(
                PermanentChristmasCostumeItemId,
                out var template))
        {
            return [];
        }

        ValidatePermanentChristmasCostume(template);
        return
        [
            new CostumeDeveloperGrant(
                template.Id,
                new DeveloperGrantMaterialDefinition(
                    template.Id,
                    template.DisplayName,
                    StackCap: 1,
                    GrantedBound: 1))
        ];
    }

    private static void ValidatePermanentChristmasCostume(
        ItemTemplateDefinition template)
    {
        if (!template.Kind.Equals("stylish", StringComparison.Ordinal) ||
            !template.NameKey.Equals("Maid8068", StringComparison.Ordinal) ||
            !template.DisplayName.Equals(
                "Christmas Suit(perpetual)",
                StringComparison.Ordinal) ||
            template.EquipmentSlot != 12 ||
            !template.ClassIds.SequenceEqual(
                new short[] { 0, 1, 2, 3 }) ||
            template.MinLevel != 1 ||
            template.MaxLevel != 200 ||
            template.Hand.HasValue ||
            template.SkillFlag != 19 ||
            !template.Texture.Equals(
                "./Localization/en_us/UI/Texture/Icon.gwo",
                StringComparison.Ordinal) ||
            !template.Icon.Equals("720,180", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Costume {template.Id} does not match stock-client content.");
        }

        using var document = JsonDocument.Parse(template.StatsJson);
        var root = document.RootElement;
        if (!HasString(root, "ID", "8068") ||
            !HasString(root, "Type", "stylish") ||
            !HasString(root, "Overlap", "1") ||
            !HasString(root, "BindType", "2") ||
            !HasString(root, "Equip", "1") ||
            !HasString(
                root,
                "PartId",
                "8061,0,8062,8063,8064,0,8065,8066,0,0,0,0") ||
            root.TryGetProperty("ExpiredTime", out _) ||
            root.TryGetProperty("SexBind", out _))
        {
            throw new InvalidOperationException(
                $"Costume {template.Id} is not the reviewed permanent unisex costume.");
        }
    }

    private static bool HasString(
        JsonElement root,
        string property,
        string expected) =>
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() == expected;

    private static Dictionary<string, DeveloperGrantMaterialDefinition>
        CreateCostumeAliases(
            IReadOnlyList<CostumeDeveloperGrant> costumes)
    {
        var aliases = new Dictionary<string, DeveloperGrantMaterialDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var costume in costumes)
        {
            AddAlias(aliases, "christmassuit", costume.Grant);
            AddAlias(aliases, "christmassuitperpetual", costume.Grant);
            AddAlias(aliases, $"costume{costume.ItemId}", costume.Grant);
            AddAlias(aliases, costume.Grant.DisplayName, costume.Grant);
        }

        return aliases;
    }

    private static IReadOnlyList<SocketSpellDeveloperGrant>
        CreateSocketSpellGrants(IItemTemplateCatalog templates)
    {
        const uint firstItemId = 4270;
        const uint lastItemId = 4273;
        var values = new List<SocketSpellDeveloperGrant>(4);
        for (var itemId = firstItemId; itemId <= lastItemId; itemId++)
        {
            if (!templates.TryGet(itemId, out var template))
            {
                continue;
            }

            var ordinal = checked((int)(itemId - firstItemId + 1));
            ValidateSocketSpellTemplate(template, ordinal);
            values.Add(new SocketSpellDeveloperGrant(
                itemId,
                ordinal,
                new DeveloperGrantMaterialDefinition(
                    itemId,
                    template.DisplayName,
                    ReadSocketSpellStackCap(template),
                    GrantedBound: 0)));
        }

        if (values.Count is not 0 and not 4)
        {
            throw new InvalidOperationException(
                "The pinned Socket Spell item family is incomplete.");
        }

        return values;
    }

    private static void ValidateSocketSpellTemplate(
        ItemTemplateDefinition template,
        int ordinal)
    {
        var expectedName = ordinal switch
        {
            1 => "Socket Spell I",
            2 => "Socket Spell II",
            3 => "Socket Spell III",
            4 => "Socket Spell IV",
            _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
        };
        if (!template.Kind.Equals("consume item", StringComparison.Ordinal) ||
            !template.NameKey.Equals(
                $"Smithing{template.Id}",
                StringComparison.Ordinal) ||
            !template.DisplayName.Equals(expectedName, StringComparison.Ordinal) ||
            !template.Texture.Equals(
                "./Localization/en_us/UI/Texture/Icon.gwo",
                StringComparison.Ordinal) ||
            !template.Icon.Equals("108,900", StringComparison.Ordinal) ||
            template.ClassIds.Count != 0 ||
            template.MinLevel.HasValue ||
            template.MaxLevel.HasValue ||
            template.Hand.HasValue ||
            template.SkillFlag.HasValue)
        {
            throw new InvalidOperationException(
                $"Socket Spell {template.Id} does not match stock-client content.");
        }
    }

    private static short ReadSocketSpellStackCap(
        ItemTemplateDefinition template)
    {
        using var document = JsonDocument.Parse(template.StatsJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("Overlap", out var overlap) ||
            overlap.ValueKind != JsonValueKind.String ||
            !short.TryParse(
                overlap.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var stackCap) ||
            stackCap != 99 ||
            root.TryGetProperty("BindType", out _))
        {
            throw new InvalidOperationException(
                $"Socket Spell {template.Id} has invalid stack or binding metadata.");
        }

        return stackCap;
    }

    private static Dictionary<string, DeveloperGrantMaterialDefinition>
        CreateSocketSpellAliases(
            IReadOnlyList<SocketSpellDeveloperGrant> socketSpells)
    {
        var aliases = new Dictionary<string, DeveloperGrantMaterialDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var spell in socketSpells)
        {
            AddAlias(aliases, $"socketspell{spell.Ordinal}", spell.Grant);
            AddAlias(aliases, spell.Grant.DisplayName, spell.Grant);
        }

        return aliases;
    }

    private static HolyBoxDeveloperGrant CreateHolyBoxGrant(
        IItemTemplateCatalog templates,
        HolySuitConsumableDefinition box)
    {
        if (!templates.TryGet(box.ItemId, out var template))
        {
            throw new InvalidOperationException(
                $"Holy Box {box.ItemId} has no item template in the pinned revision.");
        }

        const string originalNamePrefix = "Congregation";
        if (!template.NameKey.StartsWith(
                originalNamePrefix,
                StringComparison.Ordinal) ||
            !int.TryParse(
                template.NameKey[originalNamePrefix.Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ordinal) ||
            ordinal is < 1 or > 5)
        {
            throw new InvalidOperationException(
                $"Holy Box {box.ItemId} has no canonical database ordinal.");
        }

        return new HolyBoxDeveloperGrant(
            ordinal,
            new DeveloperGrantMaterialDefinition(
                box.ItemId,
                template.DisplayName,
                box.StackCap,
                box.GrantedBound));
    }

    private static Dictionary<string, DeveloperGrantMaterialDefinition>
        CreateHolyBoxAliases(
            IReadOnlyList<HolyBoxDeveloperGrant> holyBoxes)
    {
        var aliases = new Dictionary<string, DeveloperGrantMaterialDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var holyBox in holyBoxes)
        {
            var box = holyBox.Grant;
            AddAlias(aliases, $"holybox{holyBox.Ordinal}", box);
            AddAlias(aliases, $"emptyholybox{holyBox.Ordinal}", box);
            AddAlias(aliases, box.DisplayName, box);
        }

        return aliases;
    }

    private static void AddAlias(
        IDictionary<string, DeveloperGrantMaterialDefinition> aliases,
        string alias,
        DeveloperGrantMaterialDefinition item)
    {
        var normalized = NormalizeAlias(alias);
        if (aliases.TryGetValue(normalized, out var existing) &&
            existing.ItemId != item.ItemId)
        {
            throw new InvalidOperationException(
                $"Developer item alias '{alias}' is ambiguous.");
        }

        aliases[normalized] = item;
    }

    private static string NormalizeAlias(string alias) =>
        string.Concat((alias ?? string.Empty)
            .Where(char.IsLetterOrDigit))
            .ToLowerInvariant();

    private sealed record HolyBoxDeveloperGrant(
        int Ordinal,
        DeveloperGrantMaterialDefinition Grant);

    private sealed record SocketSpellDeveloperGrant(
        uint ItemId,
        int Ordinal,
        DeveloperGrantMaterialDefinition Grant);

    private sealed record CostumeDeveloperGrant(
        uint ItemId,
        DeveloperGrantMaterialDefinition Grant);
}
