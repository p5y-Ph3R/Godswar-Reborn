using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal static partial class GearEnhancementPlanner
{
    private static bool TryMutateEquipment(
        GearEnhancementOperation operation,
        CompactItemEntry equipment,
        GearEnhancementMaterialDefinition stone,
        GearEnhancementMaterialDefinition catalyst,
        int chainAnchorIndex,
        out CompactItemEntry updated,
        out GearEnhancementStatus status,
        out string reason)
    {
        return operation switch
        {
            GearEnhancementOperation.Add => TryAdd(
                equipment,
                stone,
                chainAnchorIndex,
                out updated,
                out status,
                out reason),
            GearEnhancementOperation.Enhance => TryEnhance(
                equipment,
                stone,
                catalyst,
                chainAnchorIndex,
                out updated,
                out status,
                out reason),
            GearEnhancementOperation.Delete => TryDelete(
                equipment,
                stone,
                chainAnchorIndex,
                out updated,
                out status,
                out reason),
            _ => Fail(
                equipment,
                GearEnhancementStatus.UnsupportedOperation,
                "Gear-enhancement operation is not supported.",
                out updated,
                out status,
                out reason)
        };
    }

    private static bool TryAdd(
        CompactItemEntry equipment,
        GearEnhancementMaterialDefinition stone,
        int chainAnchorIndex,
        out CompactItemEntry updated,
        out GearEnhancementStatus status,
        out string reason)
    {
        var attributes = GetAttributes(equipment);
        var levels = GetAttributeLevels(equipment);
        if (attributes.Any(attribute =>
                attribute.HasValue && stone.AllowedAttributeIds.Contains(attribute.Value)))
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeAlreadyPresent,
                "Gear already has an attribute represented by this Attribute Stone.",
                out updated,
                out status,
                out reason);
        }

        var emptyIndex = Array.FindIndex(attributes, static attribute => !attribute.HasValue);
        if (emptyIndex < 0)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeSlotsFull,
                "Gear already has the maximum five appended attributes.",
                out updated,
                out status,
                out reason);
        }

        attributes[emptyIndex] = stone.AllowedAttributeIds[chainAnchorIndex];
        levels[emptyIndex] = 1;
        updated = WithAttributes(equipment, attributes, levels);
        status = GearEnhancementStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool TryEnhance(
        CompactItemEntry equipment,
        GearEnhancementMaterialDefinition stone,
        GearEnhancementMaterialDefinition quartz,
        int chainAnchorIndex,
        out CompactItemEntry updated,
        out GearEnhancementStatus status,
        out string reason)
    {
        if (!stone.CanEnhance)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeNotEnhanceable,
                "This Attribute Stone is add/delete-only and cannot be used with a Quartz Plate.",
                out updated,
                out status,
                out reason);
        }

        var attributes = GetAttributes(equipment);
        var levels = GetAttributeLevels(equipment);
        var matches = FindMatchingAttributeIndexes(attributes, stone, chainAnchorIndex);
        if (matches.Count == 0)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeMissing,
                "Gear does not have an attribute represented by this Attribute Stone.",
                out updated,
                out status,
                out reason);
        }

        if (matches.Count != 1)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeAmbiguous,
                "Gear has more than one attribute represented by this Attribute Stone.",
                out updated,
                out status,
                out reason);
        }

        var slot = matches[0];
        var actualChainIndex = -1;
        for (var index = chainAnchorIndex; index < stone.AllowedAttributeIds.Count; index++)
        {
            if (attributes[slot] == stone.AllowedAttributeIds[index])
            {
                actualChainIndex = index;
                break;
            }
        }

        var inferredLevel = actualChainIndex - chainAnchorIndex + 1;
        if (actualChainIndex < chainAnchorIndex || inferredLevel is < 1 or > 5)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeLevelMismatch,
                "The matching gear attribute is outside the supported enhancement chain.",
                out updated,
                out status,
                out reason);
        }

        var storedLevel = levels[slot];
        if (storedLevel.HasValue && storedLevel.Value != inferredLevel)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeLevelMismatch,
                "The matching attribute template ID and stored attribute level are not synchronized.",
                out updated,
                out status,
                out reason);
        }

        var level = checked((short)inferredLevel);
        if (quartz.SourceAttributeLevel != level ||
            quartz.TargetAttributeLevel != level + 1)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.QuartzLevelMismatch,
                "The Quartz Plate does not match the attribute's current level.",
                out updated,
                out status,
                out reason);
        }

        var targetChainIndex = actualChainIndex + 1;
        if (level >= 5 || targetChainIndex >= stone.AllowedAttributeIds.Count)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeMaximumLevel,
                "The matching attribute has no supported next template ID.",
                out updated,
                out status,
                out reason);
        }

        attributes[slot] = stone.AllowedAttributeIds[targetChainIndex];
        levels[slot] = quartz.TargetAttributeLevel;
        updated = WithAttributes(equipment, attributes, levels);
        status = GearEnhancementStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool TryDelete(
        CompactItemEntry equipment,
        GearEnhancementMaterialDefinition stone,
        int chainAnchorIndex,
        out CompactItemEntry updated,
        out GearEnhancementStatus status,
        out string reason)
    {
        var attributes = GetAttributes(equipment);
        var levels = GetAttributeLevels(equipment);
        var matches = FindMatchingAttributeIndexes(attributes, stone, chainAnchorIndex);
        if (matches.Count == 0)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeMissing,
                "Gear does not have an attribute represented by this Attribute Stone.",
                out updated,
                out status,
                out reason);
        }

        if (matches.Count != 1)
        {
            return Fail(
                equipment,
                GearEnhancementStatus.AttributeAmbiguous,
                "Gear has more than one attribute represented by this Attribute Stone.",
                out updated,
                out status,
                out reason);
        }

        var compactAttributes = new List<int?>(MaximumAttributeSlots);
        var compactLevels = new List<short?>(MaximumAttributeSlots);
        for (var index = 0; index < MaximumAttributeSlots; index++)
        {
            if (index == matches[0] || !attributes[index].HasValue)
            {
                continue;
            }

            compactAttributes.Add(attributes[index]);
            compactLevels.Add(levels[index]);
        }

        while (compactAttributes.Count < MaximumAttributeSlots)
        {
            compactAttributes.Add(null);
            compactLevels.Add(null);
        }

        updated = WithAttributes(equipment, compactAttributes.ToArray(), compactLevels.ToArray());
        status = GearEnhancementStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static List<int> FindMatchingAttributeIndexes(
        int?[] attributes,
        GearEnhancementMaterialDefinition stone,
        int chainAnchorIndex)
    {
        var validIds = stone.AllowedAttributeIds.Skip(chainAnchorIndex).ToHashSet();
        var matches = new List<int>(MaximumAttributeSlots);
        for (var index = 0; index < attributes.Length; index++)
        {
            if (attributes[index].HasValue && validIds.Contains(attributes[index]!.Value))
            {
                matches.Add(index);
            }
        }

        return matches;
    }

    private static bool TryResolveChainAnchor(
        GearEnhancementEquipmentRule equipment,
        GearEnhancementMaterialDefinition stone,
        out int chainAnchorIndex)
    {
        for (var index = 0; index < stone.AllowedAttributeIds.Count; index++)
        {
            if (equipment.AllowedAttributeIds.Contains(stone.AllowedAttributeIds[index]))
            {
                chainAnchorIndex = index;
                return true;
            }
        }

        chainAnchorIndex = -1;
        return false;
    }

    private static bool CatalystMatches(
        GearEnhancementOperation operation,
        GearEnhancementMaterialDefinition catalyst)
    {
        return operation switch
        {
            GearEnhancementOperation.Add => catalyst.Kind == GearEnhancementMaterialKind.FlameSpark,
            GearEnhancementOperation.Enhance => catalyst.Kind == GearEnhancementMaterialKind.QuartzPlate,
            GearEnhancementOperation.Delete => catalyst.Kind == GearEnhancementMaterialKind.WaterGrain,
            _ => false
        };
    }

    private static bool HasValidAttributeShape(CompactItemEntry equipment)
    {
        var attributes = GetAttributes(equipment);
        var levels = GetAttributeLevels(equipment);
        for (var index = 0; index < MaximumAttributeSlots; index++)
        {
            if (attributes[index] is < 0)
            {
                return false;
            }

            if (!attributes[index].HasValue && levels[index].HasValue)
            {
                return false;
            }
        }

        return true;
    }

    private static int?[] GetAttributes(CompactItemEntry equipment)
    {
        return
        [
            equipment.Attribute1,
            equipment.Attribute2,
            equipment.Attribute3,
            equipment.Attribute4,
            equipment.Attribute5
        ];
    }

    private static short?[] GetAttributeLevels(CompactItemEntry equipment)
    {
        return
        [
            equipment.AttributeLevel1,
            equipment.AttributeLevel2,
            equipment.AttributeLevel3,
            equipment.AttributeLevel4,
            equipment.AttributeLevel5
        ];
    }

    private static CompactItemEntry WithAttributes(
        CompactItemEntry equipment,
        int?[] attributes,
        short?[] levels)
    {
        return equipment with
        {
            Attribute1 = attributes[0],
            Attribute2 = attributes[1],
            Attribute3 = attributes[2],
            Attribute4 = attributes[3],
            Attribute5 = attributes[4],
            AttributeLevel1 = levels[0],
            AttributeLevel2 = levels[1],
            AttributeLevel3 = levels[2],
            AttributeLevel4 = levels[3],
            AttributeLevel5 = levels[4]
        };
    }

    private static CompactItemEntry ConsumeOne(CompactItemEntry item)
    {
        return item.Stack == 1
            ? CompactItemEntry.Empty
            : item with { Stack = checked((short)(item.Stack - 1)) };
    }

    private static string SetOrClear(string kitBag, int slot, CompactItemEntry item)
    {
        return item.IsEmpty
            ? KitBagSlots.ClearSlot(kitBag, slot)
            : KitBagSlots.SetSlot(kitBag, slot, item.ToCompactString());
    }

    private static GearEnhancementResult Reject(
        GearEnhancementStatus status,
        GearEnhancementOperation? operation,
        string originalKitBag,
        CompactItemEntry equipment,
        string reason)
    {
        return new GearEnhancementResult(
            status,
            operation,
            originalKitBag,
            originalKitBag,
            equipment,
            equipment,
            [],
            reason);
    }

    private static bool Fail(
        CompactItemEntry equipment,
        GearEnhancementStatus failureStatus,
        string failureReason,
        out CompactItemEntry updated,
        out GearEnhancementStatus status,
        out string reason)
    {
        updated = equipment;
        status = failureStatus;
        reason = failureReason;
        return false;
    }

    private static GearEnhancementEquipmentRule CreateEquipmentRule(
        ItemTemplateDefinition template)
    {
        var allowed = new HashSet<int>();
        try
        {
            using var document = JsonDocument.Parse(template.StatsJson);
            if (document.RootElement.TryGetProperty("MainAttribute", out var mainAttribute) &&
                mainAttribute.ValueKind == JsonValueKind.String)
            {
                foreach (var part in (mainAttribute.GetString() ?? string.Empty)
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, out var attributeId) && attributeId >= 0)
                    {
                        allowed.Add(attributeId);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A malformed template is unsupported and therefore fails closed.
        }

        return new GearEnhancementEquipmentRule(checked((uint)template.Id), allowed);
    }

    private sealed record GearEnhancementEquipmentRule(
        uint ItemId,
        IReadOnlySet<int> AllowedAttributeIds);
}
