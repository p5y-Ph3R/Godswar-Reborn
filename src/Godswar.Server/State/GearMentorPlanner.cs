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
internal static partial class GearMentorPlanner
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
            // Level 5 is a local material tier. Two Level 4 Crystals preserve
            // almost exactly the same forge contribution (25 versus 24).
            [4234] = (4233, 2),
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
}
