using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal enum ClassSuitConversionOperation : short
{
    ExchangeTierI = 100,
    ConvertToCommon = 104,
    UpgradeTierII = 105,
    UpgradeTierIII = 106,
    UpgradeTierIV = 108
}

internal enum ClassSuitConversionStatus
{
    Succeeded,
    RequestMissing,
    UnsupportedOperation,
    InvalidProfession,
    InvalidKitBagSlot,
    DuplicateKitBagSlot,
    SelectionMissing,
    StaleSelection,
    InvalidEquipment,
    UnsupportedSource,
    ProfessionMismatch,
    UnsupportedReverseTier,
    ContentMismatch,
    PlayerLevelTooLow,
    InvalidInsignia,
    InsufficientInsignia,
    InsufficientCapacity
}

internal enum ClassSuitMaterialDirection
{
    Consumed,
    Granted
}

internal sealed record ClassSuitSlotSelection(
    int KitBagSlot,
    CompactItemEntry ExpectedItem)
{
    public static ClassSuitSlotSelection Capture(
        string kitBag,
        int kitBagSlot) =>
        new(kitBagSlot, KitBagSlots.GetItem(kitBag, kitBagSlot));
}

internal sealed record ClassSuitConversionRequest(
    ClassSuitConversionOperation Operation,
    ClassSuitSlotSelection Gear,
    ClassSuitSlotSelection? Insignia = null);

internal sealed record ClassSuitSlotMutation(
    int KitBagSlot,
    CompactItemEntry Before,
    CompactItemEntry After);

internal sealed record ClassSuitMaterialChange(
    uint ItemId,
    int Quantity,
    short Bound,
    ClassSuitMaterialDirection Direction);

internal sealed record ClassSuitConversionResult(
    ClassSuitConversionStatus Status,
    ClassSuitConversionOperation? Operation,
    string OriginalKitBag,
    string UpdatedKitBag,
    CompactItemEntry EquipmentBefore,
    CompactItemEntry EquipmentAfter,
    IReadOnlyList<ClassSuitSlotMutation> Mutations,
    IReadOnlyList<ClassSuitMaterialChange> Materials,
    string? RejectionReason = null)
{
    public bool Committed => Status == ClassSuitConversionStatus.Succeeded;
}

/// <summary>
/// Plans one complete, atomic Class Suit inventory mutation. Persistence must
/// call this with the locked character's profession, level, and bag rather
/// than values echoed by the client.
/// </summary>
internal static partial class ClassSuitConversionPlanner
{
    private const int KitBagSlotCount = 96;
    private const int InsigniaStackCap = 99;

    public static ClassSuitConversionResult Create(
        IItemTemplateCatalog templates,
        string kitBag,
        byte profession,
        int playerLevel,
        ClassSuitConversionRequest? request)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var originalKitBag = kitBag ?? string.Empty;
        var normalizedKitBag = string.IsNullOrWhiteSpace(kitBag)
            ? GameDefaults.EmptyKitBag
            : kitBag;

        if (request is null)
        {
            return Reject(
                ClassSuitConversionStatus.RequestMissing,
                null,
                originalKitBag,
                CompactItemEntry.Empty,
                "Class Suit conversion request was missing.");
        }

        if (!Enum.IsDefined(request.Operation))
        {
            return Reject(
                ClassSuitConversionStatus.UnsupportedOperation,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "Class Suit conversion operation is not supported.");
        }

        if (profession > 3)
        {
            return Reject(
                ClassSuitConversionStatus.InvalidProfession,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "The authoritative character profession is invalid.");
        }

        if (request.Gear is null)
        {
            return Reject(
                ClassSuitConversionStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "Select the gear to convert.");
        }

        var isReverse = request.Operation ==
            ClassSuitConversionOperation.ConvertToCommon;
        if (isReverse != (request.Insignia is null))
        {
            return Reject(
                ClassSuitConversionStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                isReverse
                    ? "Converting to common equipment does not accept an insignia input."
                    : "Select the required Promotional Insignia stack.");
        }

        var selectedSlots = request.Insignia is null
            ? new[] { request.Gear.KitBagSlot }
            : new[]
            {
                request.Gear.KitBagSlot,
                request.Insignia.KitBagSlot
            };
        if (selectedSlots.Any(static slot =>
                slot is < 0 or >= KitBagSlotCount))
        {
            return Reject(
                ClassSuitConversionStatus.InvalidKitBagSlot,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "A Class Suit selection used an invalid kit-bag slot.");
        }

        if (selectedSlots.Distinct().Count() != selectedSlots.Length)
        {
            return Reject(
                ClassSuitConversionStatus.DuplicateKitBagSlot,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "Gear and insignias must occupy different kit-bag slots.");
        }

        var before = Enumerable.Range(0, KitBagSlotCount)
            .Select(slot => KitBagSlots.GetItem(normalizedKitBag, slot))
            .ToArray();
        var equipment = before[request.Gear.KitBagSlot];
        if (equipment.IsEmpty ||
            (request.Insignia is not null &&
             before[request.Insignia.KitBagSlot].IsEmpty))
        {
            return Reject(
                ClassSuitConversionStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                equipment,
                "A selected Class Suit item is no longer in the bag.");
        }

        if (equipment != request.Gear.ExpectedItem ||
            (request.Insignia is not null &&
             before[request.Insignia.KitBagSlot] !=
             request.Insignia.ExpectedItem))
        {
            return Reject(
                ClassSuitConversionStatus.StaleSelection,
                request.Operation,
                originalKitBag,
                equipment,
                "A selected Class Suit item changed after it was staged.");
        }

        if (equipment.Stack != 1 ||
            !templates.TryGet(equipment.Id, out var sourceTemplate) ||
            !EquipmentSlots.IsEquipmentKind(sourceTemplate.Kind) ||
            !EquipmentSlots.IsEquipmentSlot(sourceTemplate.EquipmentSlot))
        {
            return Reject(
                ClassSuitConversionStatus.InvalidEquipment,
                request.Operation,
                originalKitBag,
                equipment,
                "Only one genuine non-stackable equipment item can be converted.");
        }

        var working = before.ToArray();
        var materials = new List<ClassSuitMaterialChange>();
        CompactItemEntry equipmentAfter;
        ClassSuitConversionStatus failureStatus;
        string failureReason;
        var planned = isReverse
            ? TryPlanReverse(
                templates,
                working,
                request.Gear.KitBagSlot,
                equipment,
                sourceTemplate,
                profession,
                materials,
                out equipmentAfter,
                out failureStatus,
                out failureReason)
            : TryPlanForward(
                templates,
                working,
                request,
                equipment,
                sourceTemplate,
                profession,
                playerLevel,
                materials,
                out equipmentAfter,
                out failureStatus,
                out failureReason);
        if (!planned)
        {
            return Reject(
                failureStatus,
                request.Operation,
                originalKitBag,
                equipment,
                failureReason);
        }

        var updatedKitBag = normalizedKitBag;
        var mutations = new List<ClassSuitSlotMutation>();
        for (var slot = 0; slot < KitBagSlotCount; slot++)
        {
            if (before[slot] == working[slot])
            {
                continue;
            }

            updatedKitBag = working[slot].IsEmpty
                ? KitBagSlots.ClearSlot(updatedKitBag, slot)
                : KitBagSlots.SetSlot(
                    updatedKitBag,
                    slot,
                    working[slot].ToCompactString());
            mutations.Add(new ClassSuitSlotMutation(
                slot,
                before[slot],
                working[slot]));
        }

        return new ClassSuitConversionResult(
            ClassSuitConversionStatus.Succeeded,
            request.Operation,
            originalKitBag,
            updatedKitBag,
            equipment,
            equipmentAfter,
            mutations,
            materials);
    }
}
