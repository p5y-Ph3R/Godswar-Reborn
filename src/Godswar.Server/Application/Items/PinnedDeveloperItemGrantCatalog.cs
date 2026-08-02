using System.Collections.Frozen;
using System.Globalization;

namespace Godswar.Server.Application.Items;

/// <summary>
/// The deliberately narrow developer-grant allowlist for one immutable item
/// content revision. Existing material grants are delegated to their native
/// catalog; empty Holy Boxes are derived from the same published Holy Suit
/// revision used by gameplay.
/// </summary>
internal sealed class PinnedDeveloperItemGrantCatalog :
    IDeveloperItemGrantCatalog
{
    private readonly IItemMaterialCatalog _materials;
    private readonly FrozenDictionary<uint, DeveloperGrantMaterialDefinition>
        _holyBoxesById;
    private readonly FrozenDictionary<string, DeveloperGrantMaterialDefinition>
        _holyBoxesByAlias;

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
    }

    public bool TryResolveDeveloper(
        uint itemId,
        out DeveloperGrantMaterialDefinition item) =>
        _materials.TryResolveDeveloper(itemId, out item!) ||
        _holyBoxesById.TryGetValue(itemId, out item!);

    public bool TryResolveDeveloper(
        string alias,
        out DeveloperGrantMaterialDefinition item) =>
        _materials.TryResolveDeveloper(alias, out item!) ||
        _holyBoxesByAlias.TryGetValue(NormalizeAlias(alias), out item!);

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
}
