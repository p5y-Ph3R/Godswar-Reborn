using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal readonly record struct FashionAppearanceDefinition(
    uint ItemId,
    ImmutableArray<uint> PartIds,
    uint? PartHair)
{
    public uint ResolveHair(byte characterHair)
    {
        return PartHair is { } partHair
            ? (partHair * 10u) + (uint)(characterHair % 10)
            : characterHair;
    }
}

/// <summary>
/// Immutable native-avatar projections parsed once from the process-pinned
/// stylish-item revision. Malformed optional content is excluded so packet
/// builders can safely retain the ordinary equipment appearance.
/// </summary>
internal sealed class FashionAppearanceCatalog
{
    public const int PartCount = 12;

    private const uint MaximumPartHair = (uint.MaxValue - 9u) / 10u;

    private readonly FrozenDictionary<uint, FashionAppearanceDefinition>
        _byItemId;

    public FashionAppearanceCatalog(IItemTemplateCatalog templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var definitions = new Dictionary<uint, FashionAppearanceDefinition>();
        foreach (var template in templates.All)
        {
            if (template.EquipmentSlot != EquipmentSlots.Stylish ||
                !string.Equals(
                    template.Kind,
                    "stylish",
                    StringComparison.OrdinalIgnoreCase) ||
                !TryCreate(template, out var definition))
            {
                continue;
            }

            definitions[definition.ItemId] = definition;
        }

        _byItemId = definitions.ToFrozenDictionary();
    }

    public int Count => _byItemId.Count;

    public bool TryGet(
        uint itemId,
        out FashionAppearanceDefinition definition) =>
        _byItemId.TryGetValue(itemId, out definition);

    private static bool TryCreate(
        ItemTemplateDefinition template,
        out FashionAppearanceDefinition definition)
    {
        definition = default;
        try
        {
            using var document = JsonDocument.Parse(template.StatsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("PartId", out var partIdsElement) ||
                partIdsElement.ValueKind != JsonValueKind.String ||
                !TryParseParts(partIdsElement.GetString(), out var partIds) ||
                !TryReadPartHair(document.RootElement, out var partHair))
            {
                return false;
            }

            definition = new FashionAppearanceDefinition(
                template.Id,
                partIds,
                partHair);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseParts(
        string? source,
        out ImmutableArray<uint> partIds)
    {
        partIds = default;
        if (source is null)
        {
            return false;
        }

        var tokens = source.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length != PartCount)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<uint>(PartCount);
        foreach (var token in tokens)
        {
            if (!uint.TryParse(
                    token,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var partId))
            {
                return false;
            }

            builder.Add(partId);
        }

        partIds = builder.MoveToImmutable();
        return true;
    }

    private static bool TryReadPartHair(
        JsonElement root,
        out uint? partHair)
    {
        partHair = null;
        if (!root.TryGetProperty("PartHair", out var element))
        {
            return true;
        }

        uint parsed;
        if (element.ValueKind == JsonValueKind.String)
        {
            if (!uint.TryParse(
                    element.GetString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed))
            {
                return false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetUInt32(out parsed))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (parsed > MaximumPartHair)
        {
            return false;
        }

        partHair = parsed;
        return true;
    }
}
