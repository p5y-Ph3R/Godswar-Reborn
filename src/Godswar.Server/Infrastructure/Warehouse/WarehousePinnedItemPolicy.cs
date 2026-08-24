using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Warehouse;

namespace Godswar.Server.Infrastructure.Warehouse;

internal static class WarehousePinnedItemPolicy
{
    public static bool IsValid(
        IItemTemplateCatalog templates,
        WarehouseExpansionPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        foreach (var group in policy.Levels
                     .Where(static level => level.KeyCost > 0)
                     .GroupBy(static level => level.KeyItemId))
        {
            if (!templates.TryGet(checked((uint)group.Key), out var template) ||
                !string.Equals(
                    template.Kind,
                    "consume item",
                    StringComparison.Ordinal) ||
                !TryReadPositiveInteger(
                    template.StatsJson,
                    "Overlap",
                    out var stackCap) ||
                stackCap < group.Max(static level => level.KeyCost))
            {
                return false;
            }
        }

        return true;
    }

    public static int ReadStackCap(
        IItemTemplateCatalog templates,
        int itemId)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (itemId <= 0 ||
            !templates.TryGet(checked((uint)itemId), out var template) ||
            !TryReadPositiveInteger(
                template.StatsJson,
                "Overlap",
                out var stackCap))
        {
            throw new InvalidDataException(
                $"Pinned item {itemId} has no bounded stack cap.");
        }
        return stackCap;
    }

    private static bool TryReadPositiveInteger(
        string statsJson,
        string property,
        out int value)
    {
        value = 0;
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            if (!document.RootElement.TryGetProperty(
                    property,
                    out var element))
            {
                return false;
            }
            return element.ValueKind switch
            {
                JsonValueKind.String => int.TryParse(
                    element.GetString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value) && value is >= 1 and <= 99,
                JsonValueKind.Number => element.TryGetInt32(out value) &&
                    value is >= 1 and <= 99,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
