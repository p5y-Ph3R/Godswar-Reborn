namespace Godswar.Server.State;

internal static partial class GearMentorPlanner
{
    private static bool PlanDecomposition(
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
                !EquipmentTemplates.TryGetValue(equipment.Id, out var template))
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

            var candidates = GetAttributeMatchedDusts(equipment);
            if (candidates.Count == 0)
            {
                // Some eligible native drops have no appended attribute. The
                // client establishes that decomposition still returns random
                // Dust, but it does not ship the original server's drop table.
                candidates = GearMentorMaterialCatalog.AttributeDusts;
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
                GearMentorMaterialCatalog.StackCap);
            outputs.Add(new GearMentorOutput(dust.ItemId, quantity, equipment.Bound));
            working[selection.KitBagSlot] = CompactItemEntry.Empty;
        }

        status = GearMentorStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool PlanAttributeStone(
        CompactItemEntry[] working,
        GearMentorSlotSelection selection,
        List<GearMentorOutput> outputs,
        out GearMentorStatus status,
        out string reason)
    {
        var dustItem = working[selection.KitBagSlot];
        if (!GearMentorMaterialCatalog.TryGetDust(dustItem.Id, out var dust))
        {
            return Fail(
                GearMentorStatus.InvalidDust,
                "Only native Attribute Dust can be made into an Attribute Stone.",
                out status,
                out reason);
        }

        if (dustItem.Stack < GearMentorMaterialCatalog.StoneRecipeDustQuantity)
        {
            return Fail(
                GearMentorStatus.InsufficientDust,
                "Exactly 99 matching Dust are required to make one Attribute Stone.",
                out status,
                out reason);
        }

        working[selection.KitBagSlot] = Consume(
            dustItem,
            GearMentorMaterialCatalog.StoneRecipeDustQuantity);
        outputs.Add(new GearMentorOutput(dust.AttributeStoneItemId, 1, dustItem.Bound));
        status = GearMentorStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool PlanCrystalTransform(
        CompactItemEntry[] working,
        GearMentorSlotSelection selection,
        List<GearMentorOutput> outputs,
        out GearMentorStatus status,
        out string reason)
    {
        var crystal = working[selection.KitBagSlot];
        if (!CrystalTransforms.TryGetValue(crystal.Id, out var recipe) || crystal.Stack < 1)
        {
            return Fail(
                GearMentorStatus.InvalidCrystal,
                "Only supported Level 2, Level 3, Level 4, or Level 5 Crystals can be transformed into lower-level Crystals.",
                out status,
                out reason);
        }

        working[selection.KitBagSlot] = Consume(crystal, 1);
        outputs.Add(new GearMentorOutput(recipe.ResultItemId, recipe.Quantity, crystal.Bound));
        status = GearMentorStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool PlanGemPieceCombination(
        CompactItemEntry[] working,
        GearMentorSlotSelection selection,
        List<GearMentorOutput> outputs,
        out GearMentorStatus status,
        out string reason)
    {
        const int requiredPieces = 99;
        var pieces = working[selection.KitBagSlot];
        if (!GemPieceRecipes.TryGetValue(pieces.Id, out var resultItemId))
        {
            return Fail(
                GearMentorStatus.InvalidGemPieces,
                "Only supported Level 4 or Level 5 gem pieces can be combined.",
                out status,
                out reason);
        }

        if (pieces.Stack < requiredPieces)
        {
            return Fail(
                GearMentorStatus.InsufficientGemPieces,
                "99 matching gem pieces are required.",
                out status,
                out reason);
        }

        working[selection.KitBagSlot] = Consume(pieces, requiredPieces);
        outputs.Add(new GearMentorOutput(resultItemId, 1, pieces.Bound));
        status = GearMentorStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static IReadOnlyList<AttributeDustDefinition> GetAttributeMatchedDusts(
        CompactItemEntry equipment)
    {
        var attributeIds = new[]
        {
            equipment.Attribute1,
            equipment.Attribute2,
            equipment.Attribute3,
            equipment.Attribute4,
            equipment.Attribute5
        };
        var dusts = new List<AttributeDustDefinition>();
        var seen = new HashSet<uint>();
        foreach (var attributeId in attributeIds)
        {
            if (attributeId.HasValue &&
                GearMentorMaterialCatalog.TryGetDustForAttribute(attributeId.Value, out var dust) &&
                seen.Add(dust.ItemId))
            {
                dusts.Add(dust);
            }
        }

        return dusts;
    }

    private static bool TryAddOutput(
        CompactItemEntry[] working,
        GearMentorOutput output)
    {
        var stackCap = ResolveStackCap(output.ItemId);
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

    private static int ResolveStackCap(uint itemId)
    {
        if (GearMentorMaterialCatalog.TryGetDust(itemId, out var dust))
        {
            return dust.StackCap;
        }

        if (GearEnhancementMaterialCatalog.TryGet(itemId, out var enhancementMaterial))
        {
            return enhancementMaterial.StackCap;
        }

        if (ForgingMaterialCatalog.TryResolve(itemId, out var forgingMaterial))
        {
            return forgingMaterial.StackCap;
        }

        throw new InvalidOperationException(
            $"Gear Mentor output item {itemId} has no authoritative material definition.");
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
