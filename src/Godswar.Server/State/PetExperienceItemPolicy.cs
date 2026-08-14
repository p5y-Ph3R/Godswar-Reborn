using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal readonly record struct PetExperienceItemDefinition(
    uint ItemId,
    long Experience,
    bool RequiresBoundPet);

/// <summary>
/// Resolves Morning Dew behavior from the process-pinned official item
/// content. Item IDs identify the narrow protocol family; value and binding
/// policy remain owned by the published database-backed item revision.
/// </summary>
internal static class PetExperienceItemPolicy
{
    public const uint FirstMorningDew = 10130;
    public const uint LastMorningDew = 10134;
    public const uint FirstRestrictedMorningDew = 10140;
    public const uint LastRestrictedMorningDew = 10144;
    public const long MaximumNativePetExperience = uint.MaxValue;

    public static bool IsMorningDew(uint itemId) =>
        itemId is >= FirstMorningDew and <= LastMorningDew or
            >= FirstRestrictedMorningDew and <= LastRestrictedMorningDew;

    public static bool TryResolve(
        IItemTemplateCatalog templates,
        uint itemId,
        out PetExperienceItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(templates);
        definition = default;
        if (!IsMorningDew(itemId))
        {
            return false;
        }
        if (!templates.TryGet(itemId, out var template))
        {
            throw new InvalidDataException(
                $"Morning Dew item {itemId} is absent from official content.");
        }

        using var document = JsonDocument.Parse(template.StatsJson);
        var stats = document.RootElement;
        var restricted = itemId >= FirstRestrictedMorningDew;
        if (template.Id != itemId ||
            !string.Equals(template.Kind, "consume item", StringComparison.Ordinal) ||
            !ReadRequired(stats, "ID").Equals(
                itemId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            ReadRequired(stats, "Use") != "1" ||
            ReadRequired(stats, "Skill") != "4721" ||
            ReadRequired(stats, "ItemType") != "18" ||
            !HasValidPetLimit(stats, restricted) ||
            !long.TryParse(
                ReadRequired(stats, "Values"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var experience) ||
            experience <= 0 ||
            experience > MaximumNativePetExperience)
        {
            throw new InvalidDataException(
                $"Morning Dew item {itemId} has invalid official metadata.");
        }

        definition = new(itemId, experience, restricted);
        return true;
    }

    private static bool HasValidPetLimit(
        JsonElement stats,
        bool restricted)
    {
        if (!stats.TryGetProperty("Petlimit", out var petLimit))
        {
            return !restricted;
        }
        return restricted &&
            petLimit.ValueKind == JsonValueKind.String &&
            petLimit.GetString() == "1";
    }

    private static string ReadRequired(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException(
                $"Morning Dew content is missing {name}.");
}
