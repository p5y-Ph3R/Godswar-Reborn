using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal static partial class GearMentorPlanner
{
    private static bool PlanDecomposition(
        IItemTemplateCatalog templates,
        CompactItemEntry[] working,
        int playerLevel,
        IReadOnlyList<GearMentorSlotSelection> selections,
        List<GearMentorOutput> outputs,
        Func<int, int> randomIndex,
        out GearMentorStatus status,
        out string reason)
    {
        if (playerLevel < MinimumDecomposePlayerLevel)
        {
            return Fail(
                GearMentorStatus.PlayerLevelTooLow,
                "Characters below Level 30 cannot decompose gear.",
                out status,
                out reason);
        }

        foreach (var selection in selections)
        {
            var equipment = working[selection.KitBagSlot];
            if (equipment.Stack != 1 ||
                !templates.TryGet(equipment.Id, out var template) ||
                !EquipmentSlots.IsEquipmentKind(template.Kind) ||
                !EquipmentSlots.IsEquipmentSlot(template.EquipmentSlot))
            {
                return Fail(
                    GearMentorStatus.InvalidEquipment,
                    "Only genuine non-stackable gear can be decomposed.",
                    out status,
                    out reason);
            }

            if (ClassSuitItemCatalog.IsClassSuit(equipment.Id))
            {
                return Fail(
                    GearMentorStatus.ClassSuit,
                    "Class Suits cannot be decomposed.",
                    out status,
                    out reason);
            }

            if (!template.MinLevel.HasValue ||
                template.MinLevel.Value < MinimumDecomposeEquipmentLevel)
            {
                return Fail(
                    GearMentorStatus.EquipmentLevelTooLow,
                    "Gear below Level 50 cannot be decomposed.",
                    out status,
                    out reason);
            }

            if (equipment.Quality < 2 && equipment.Grade < 2)
            {
                return Fail(
                    GearMentorStatus.InsufficientEquipmentQuality,
                    "Gear must be Enhanced quality or Grade 2 or higher.",
                    out status,
                    out reason);
            }

            var candidates = GetAttributeMatchedDusts(templates.Materials, equipment);
            if (candidates.Count == 0)
            {
                // Some eligible native drops have no appended attribute. The
                // client establishes that decomposition still returns random
                // Dust, but it does not ship the original server's drop table.
                candidates = templates.Materials.AttributeDusts;
            }

            var selectedIndex = randomIndex(candidates.Count);
            if (selectedIndex is < 0 || selectedIndex >= candidates.Count)
            {
                throw new InvalidOperationException(
                    $"Gear Mentor random source returned {selectedIndex} for {candidates.Count} choices.");
            }

            var dust = candidates[selectedIndex];
            // The original client exposes only the direction of the scaling
            // (higher quality gives more), not the exact server formula. Keep
            // the local rule small, monotonic, and capped to one native stack.
            var quantity = Math.Clamp(
                Math.Max(1, (int)equipment.Quality) + Math.Max(1, (int)equipment.Grade) - 1,
                1,
                dust.StackCap);
            outputs.Add(new GearMentorOutput(dust.ItemId, quantity, equipment.Bound));
            working[selection.KitBagSlot] = CompactItemEntry.Empty;
        }

        status = GearMentorStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool PlanAttributeStone(
        IItemTemplateCatalog templates,
        CompactItemEntry[] working,
        GearMentorSlotSelection selection,
        List<GearMentorOutput> outputs,
        out GearMentorStatus status,
        out string reason)
    {
        var dustItem = working[selection.KitBagSlot];
        if (!templates.Materials.TryGetDust(dustItem.Id, out var dust))
        {
            return Fail(
                GearMentorStatus.InvalidDust,
                "Only native Attribute Dust can be made into an Attribute Stone.",
                out status,
                out reason);
        }

        if (dustItem.Stack < dust.RecipeQuantity)
        {
            return Fail(
                GearMentorStatus.InsufficientDust,
                "Exactly 99 matching Dust are required to make one Attribute Stone.",
                out status,
                out reason);
        }

        working[selection.KitBagSlot] = Consume(
            dustItem,
            dust.RecipeQuantity);
        outputs.Add(new GearMentorOutput(dust.AttributeStoneItemId, 1, dustItem.Bound));
        status = GearMentorStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool PlanCrystalTransform(
        IItemMaterialCatalog materials,
        CompactItemEntry[] working,
        GearMentorSlotSelection selection,
        List<GearMentorOutput> outputs,
        out GearMentorStatus status,
        out string reason)
    {
        var crystal = working[selection.KitBagSlot];
        if (!materials.TryResolveCrystalTransform(
                crystal.Id,
                out var recipe) ||
            crystal.Stack < recipe.SourceQuantity)
        {
            return Fail(
                GearMentorStatus.InvalidCrystal,
                "Only supported Level 2, Level 3, Level 4, or Level 5 Crystals can be transformed into lower-level Crystals.",
                out status,
                out reason);
        }

        working[selection.KitBagSlot] = Consume(
            crystal,
            recipe.SourceQuantity);
        outputs.Add(new GearMentorOutput(
            recipe.TargetItemId,
            recipe.TargetQuantity,
            crystal.Bound));
        status = GearMentorStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool PlanGemPieceCombination(
        IItemMaterialCatalog materials,
        CompactItemEntry[] working,
        GearMentorSlotSelection selection,
        List<GearMentorOutput> outputs,
        out GearMentorStatus status,
        out string reason)
    {
        var pieces = working[selection.KitBagSlot];
        if (!materials.TryResolveGemPieceCombination(
                pieces.Id,
                out var recipe))
        {
            return Fail(
                GearMentorStatus.InvalidGemPieces,
                "Only supported Level 4 or Level 5 gem pieces can be combined.",
                out status,
                out reason);
        }

        if (pieces.Stack < recipe.SourceQuantity)
        {
            return Fail(
                GearMentorStatus.InsufficientGemPieces,
                $"{recipe.SourceQuantity} matching gem pieces are required.",
                out status,
                out reason);
        }

        working[selection.KitBagSlot] = Consume(
            pieces,
            recipe.SourceQuantity);
        outputs.Add(new GearMentorOutput(
            recipe.TargetItemId,
            recipe.TargetQuantity,
            pieces.Bound));
        status = GearMentorStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static IReadOnlyList<AttributeDustDefinition> GetAttributeMatchedDusts(
        IItemMaterialCatalog materials,
        CompactItemEntry equipment)
    {
        var attributeIds = new[]
        {
            equipment.Attribute1,
            equipment.Attribute2,
            equipment.Attribute3,
            equipment.Attribute4,
            equipment.Attribute5,
            equipment.ClassAttribute1,
            equipment.ClassAttribute2,
            equipment.ElementalAttribute1,
            equipment.ElementalAttribute2
        };
        var dusts = new List<AttributeDustDefinition>();
        var seen = new HashSet<uint>();
        foreach (var attributeId in attributeIds)
        {
            if (attributeId.HasValue &&
                materials.TryGetDustForAttribute(attributeId.Value, out var dust) &&
                seen.Add(dust.ItemId))
            {
                dusts.Add(dust);
            }
        }

        return dusts;
    }

    private static bool TryAddOutput(
        IItemMaterialCatalog materials,
        CompactItemEntry[] working,
        GearMentorOutput output)
    {
        var stackCap = materials.ResolveStackCap(output.ItemId);
        var remaining = output.Quantity;
        for (var slot = 0; slot < working.Length && remaining > 0; slot++)
        {
            var item = working[slot];
            if (item.IsEmpty ||
                item.Id != output.ItemId ||
                item.Bound != output.Bound ||
                item.Stack >= stackCap)
            {
                continue;
            }

            var added = Math.Min(remaining, stackCap - item.Stack);
            working[slot] = item with { Stack = checked((short)(item.Stack + added)) };
            remaining -= added;
        }

        for (var slot = 0; slot < working.Length && remaining > 0; slot++)
        {
            if (!working[slot].IsEmpty)
            {
                continue;
            }

            var added = Math.Min(remaining, stackCap);
            working[slot] = CompactItemEntry.Empty with
            {
                Id = output.ItemId,
                Quality = 1,
                Grade = 1,
                Bound = output.Bound,
                Stack = checked((short)added)
            };
            remaining -= added;
        }

        return remaining == 0;
    }

    private static CompactItemEntry Consume(CompactItemEntry item, int quantity)
    {
        var remaining = item.Stack - quantity;
        return remaining == 0
            ? CompactItemEntry.Empty
            : item with { Stack = checked((short)remaining) };
    }

    private static bool Fail(
        GearMentorStatus failureStatus,
        string failureReason,
        out GearMentorStatus status,
        out string reason)
    {
        status = failureStatus;
        reason = failureReason;
        return false;
    }

    private static GearMentorResult Reject(
        GearMentorStatus status,
        GearMentorOperation? operation,
        string originalKitBag,
        string reason)
    {
        return new GearMentorResult(
            status,
            operation,
            originalKitBag,
            originalKitBag,
            [],
            [],
            reason);
    }
}
