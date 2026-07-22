using System.Security.Cryptography;

namespace Godswar.Server.State;

internal enum GearMentorOperation
{
    Decompose = 1,
    MakeAttributeStone = 4,
    TransformCrystal = 8,
    CombineGemPieces = 201
}

internal enum GearMentorStatus
{
    Succeeded,
    RequestMissing,
    UnsupportedOperation,
    InvalidKitBagSlot,
    DuplicateKitBagSlot,
    SelectionMissing,
    StaleSelection,
    PlayerLevelTooLow,
    InvalidEquipment,
    EquipmentLevelTooLow,
    InsufficientEquipmentQuality,
    ClassSuit,
    InvalidDust,
    InsufficientDust,
    InvalidCrystal,
    InvalidGemPieces,
    InsufficientGemPieces,
    InsufficientCapacity
}

internal sealed record GearMentorSlotSelection(
    int KitBagSlot,
    CompactItemEntry ExpectedItem)
{
    public static GearMentorSlotSelection Capture(string kitBag, int kitBagSlot)
    {
        return new GearMentorSlotSelection(
            kitBagSlot,
            KitBagSlots.GetItem(kitBag, kitBagSlot));
    }
}

internal sealed record GearMentorRequest(
    GearMentorOperation Operation,
    IReadOnlyList<GearMentorSlotSelection> Selections);

internal sealed record GearMentorSlotMutation(
    int KitBagSlot,
    CompactItemEntry Before,
    CompactItemEntry After);

internal sealed record GearMentorOutput(
    uint ItemId,
    int Quantity,
    short Bound);

internal sealed record GearMentorResult(
    GearMentorStatus Status,
    GearMentorOperation? Operation,
    string OriginalKitBag,
    string UpdatedKitBag,
    IReadOnlyList<GearMentorSlotMutation> Mutations,
    IReadOnlyList<GearMentorOutput> Outputs,
    string? RejectionReason = null)
{
    public bool Committed => Status == GearMentorStatus.Succeeded;
}

/// <summary>
/// Builds all Gear Mentor inventory mutations before either persistence store
/// writes anything. The store supplies the authoritative character level and
/// bag while holding its character/inventory lock.
/// </summary>
internal static class GearMentorPlanner
{
    private const int KitBagSlotCount = 96;
    private const int MinimumDecomposePlayerLevel = 30;
    private const int MinimumDecomposeEquipmentLevel = 50;

    private static readonly IReadOnlyDictionary<uint, ItemTemplateSeed> EquipmentTemplates =
        ItemTemplateSeeds.All
            .Where(static template =>
                template.Id > 0 && EquipmentSlots.IsEquipmentSlot(template.EquipmentSlot))
            .GroupBy(static template => checked((uint)template.Id))
            .ToDictionary(static group => group.Key, static group => group.First());

    private static readonly IReadOnlyDictionary<uint, (uint ResultItemId, int Quantity)> CrystalTransforms =
        new Dictionary<uint, (uint ResultItemId, int Quantity)>
        {
            [4232] = (4231, 4),
            [4231] = (4230, 8)
        };

    private static readonly IReadOnlyDictionary<uint, uint> GemPieceRecipes =
        new Dictionary<uint, uint>
        {
            [4214] = 4213,
            [4224] = 4223,
            [4216] = 4215,
            [4226] = 4225,
            [4235] = 4234
        };

    public static GearMentorResult Create(
        string kitBag,
        int playerLevel,
        GearMentorRequest? request,
        Func<int, int>? randomIndex = null)
    {
        var originalKitBag = kitBag ?? string.Empty;
        var normalizedKitBag = string.IsNullOrWhiteSpace(kitBag)
            ? GameDefaults.EmptyKitBag
            : kitBag;

        if (request is null)
        {
            return Reject(
                GearMentorStatus.RequestMissing,
                null,
                originalKitBag,
                "Gear Mentor request was missing.");
        }

        if (request.Operation is not (
                GearMentorOperation.Decompose or
                GearMentorOperation.MakeAttributeStone or
                GearMentorOperation.TransformCrystal or
                GearMentorOperation.CombineGemPieces))
        {
            return Reject(
                GearMentorStatus.UnsupportedOperation,
                request.Operation,
                originalKitBag,
                "Gear Mentor operation is not supported.");
        }

        var minimumSelections = 1;
        var maximumSelections = request.Operation == GearMentorOperation.Decompose ? 3 : 1;
        if (request.Selections is null ||
            request.Selections.Count < minimumSelections ||
            request.Selections.Count > maximumSelections)
        {
            return Reject(
                GearMentorStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                request.Operation == GearMentorOperation.Decompose
                    ? "Select between one and three gear items."
                    : "Select exactly one material item.");
        }

        var slots = request.Selections.Select(static selection => selection.KitBagSlot).ToArray();
        if (slots.Any(static slot => slot is < 0 or >= KitBagSlotCount))
        {
            return Reject(
                GearMentorStatus.InvalidKitBagSlot,
                request.Operation,
                originalKitBag,
                "One or more selections used an invalid kit-bag slot.");
        }

        if (slots.Distinct().Count() != slots.Length)
        {
            return Reject(
                GearMentorStatus.DuplicateKitBagSlot,
                request.Operation,
                originalKitBag,
                "Each selected item must occupy a distinct kit-bag slot.");
        }

        var before = Enumerable.Range(0, KitBagSlotCount)
            .Select(slot => KitBagSlots.GetItem(normalizedKitBag, slot))
            .ToArray();
        foreach (var selection in request.Selections)
        {
            var current = before[selection.KitBagSlot];
            if (current.IsEmpty)
            {
                return Reject(
                    GearMentorStatus.SelectionMissing,
                    request.Operation,
                    originalKitBag,
                    "A selected item is no longer in the bag.");
            }

            if (current != selection.ExpectedItem)
            {
                return Reject(
                    GearMentorStatus.StaleSelection,
                    request.Operation,
                    originalKitBag,
                    "A selected item changed after the client staged it.");
            }
        }

        var working = before.ToArray();
        var outputs = new List<GearMentorOutput>();
        GearMentorStatus status;
        string reason;
        switch (request.Operation)
        {
            case GearMentorOperation.Decompose:
                if (PlanDecomposition(
                        working,
                        playerLevel,
                        request.Selections,
                        outputs,
                        randomIndex ?? RandomNumberGenerator.GetInt32,
                        out status,
                        out reason))
                {
                    status = GearMentorStatus.Succeeded;
                }
                break;
            case GearMentorOperation.MakeAttributeStone:
                if (PlanAttributeStone(
                        working,
                        request.Selections[0],
                        outputs,
                        out status,
                        out reason))
                {
                    status = GearMentorStatus.Succeeded;
                }
                break;
            case GearMentorOperation.TransformCrystal:
                if (PlanCrystalTransform(
                        working,
                        request.Selections[0],
                        outputs,
                        out status,
                        out reason))
                {
                    status = GearMentorStatus.Succeeded;
                }
                break;
            case GearMentorOperation.CombineGemPieces:
                if (PlanGemPieceCombination(
                        working,
                        request.Selections[0],
                        outputs,
                        out status,
                        out reason))
                {
                    status = GearMentorStatus.Succeeded;
                }
                break;
            default:
                status = GearMentorStatus.UnsupportedOperation;
                reason = "Gear Mentor operation is not supported.";
                break;
        }
        if (status != GearMentorStatus.Succeeded)
        {
            return Reject(status, request.Operation, originalKitBag, reason);
        }

        foreach (var output in outputs)
        {
            if (!TryAddOutput(working, output))
            {
                return Reject(
                    GearMentorStatus.InsufficientCapacity,
                    request.Operation,
                    originalKitBag,
                    "The resulting items do not fit in the kit bag.");
            }
        }

        var updatedKitBag = normalizedKitBag;
        var mutations = new List<GearMentorSlotMutation>();
        for (var slot = 0; slot < KitBagSlotCount; slot++)
        {
            if (before[slot] == working[slot])
            {
                continue;
            }

            updatedKitBag = working[slot].IsEmpty
                ? KitBagSlots.ClearSlot(updatedKitBag, slot)
                : KitBagSlots.SetSlot(updatedKitBag, slot, working[slot].ToCompactString());
            mutations.Add(new GearMentorSlotMutation(slot, before[slot], working[slot]));
        }

        return new GearMentorResult(
            GearMentorStatus.Succeeded,
            request.Operation,
            originalKitBag,
            updatedKitBag,
            mutations,
            outputs);
    }

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
                "Only Level 2 or Level 3 Crystals can be transformed into lower-level Crystals.",
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
