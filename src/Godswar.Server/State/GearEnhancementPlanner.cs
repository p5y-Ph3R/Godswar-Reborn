using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal enum GearEnhancementOperation
{
    Enhance = 2,
    Add = 3,
    Delete = 6
}

internal enum GearEnhancementStatus
{
    Succeeded,
    RequestMissing,
    UnsupportedOperation,
    InvalidKitBagSlot,
    DuplicateKitBagSlot,
    SelectionMissing,
    StaleSelection,
    InvalidEquipment,
    UnsupportedEquipment,
    InvalidAttributeState,
    InvalidAttributeStone,
    InvalidCatalyst,
    InsufficientMaterial,
    AttributeNotAllowed,
    AttributeAlreadyPresent,
    AttributeSlotsFull,
    AttributeMissing,
    AttributeAmbiguous,
    AttributeNotEnhanceable,
    AttributeLevelMismatch,
    QuartzLevelMismatch,
    AttributeMaximumLevel
}

internal sealed record GearEnhancementSlotSelection(
    int KitBagSlot,
    CompactItemEntry ExpectedItem)
{
    public static GearEnhancementSlotSelection Capture(string kitBag, int kitBagSlot)
    {
        return new GearEnhancementSlotSelection(
            kitBagSlot,
            KitBagSlots.GetItem(kitBag, kitBagSlot));
    }
}

internal sealed record GearEnhancementRequest(
    GearEnhancementOperation Operation,
    GearEnhancementSlotSelection Gear,
    GearEnhancementSlotSelection AttributeStone,
    GearEnhancementSlotSelection Catalyst);

internal sealed record GearEnhancementSlotMutation(
    int KitBagSlot,
    CompactItemEntry Before,
    CompactItemEntry After);

internal sealed record GearEnhancementResult(
    GearEnhancementStatus Status,
    GearEnhancementOperation? Operation,
    string OriginalKitBag,
    string UpdatedKitBag,
    CompactItemEntry EquipmentBefore,
    CompactItemEntry EquipmentAfter,
    IReadOnlyList<GearEnhancementSlotMutation> Mutations,
    string? RejectionReason = null)
{
    public bool Committed => Status == GearEnhancementStatus.Succeeded;
}

internal static partial class GearEnhancementPlanner
{
    private const int KitBagSlotCount = 96;
    private const int MaximumAttributeSlots = 5;

    public static GearEnhancementResult Create(
        IItemTemplateCatalog templates,
        string kitBag,
        GearEnhancementRequest? request)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var originalKitBag = kitBag ?? string.Empty;
        var workingKitBag = string.IsNullOrWhiteSpace(kitBag)
            ? GameDefaults.EmptyKitBag
            : kitBag;

        if (request is null)
        {
            return Reject(
                GearEnhancementStatus.RequestMissing,
                null,
                originalKitBag,
                CompactItemEntry.Empty,
                "Gear-enhancement request was missing.");
        }

        if (request.Operation is not (
                GearEnhancementOperation.Add or
                GearEnhancementOperation.Enhance or
                GearEnhancementOperation.Delete))
        {
            return Reject(
                GearEnhancementStatus.UnsupportedOperation,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "Gear-enhancement operation is not supported.");
        }

        if (request.Gear is null || request.AttributeStone is null || request.Catalyst is null)
        {
            return Reject(
                GearEnhancementStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "Gear, Attribute Stone, and catalyst selections are all required.");
        }

        var selectedSlots = new[]
        {
            request.Gear.KitBagSlot,
            request.AttributeStone.KitBagSlot,
            request.Catalyst.KitBagSlot
        };
        if (selectedSlots.Any(static slot => slot is < 0 or >= KitBagSlotCount))
        {
            return Reject(
                GearEnhancementStatus.InvalidKitBagSlot,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "One or more gear-enhancement selections used an invalid kit-bag slot.");
        }

        if (selectedSlots.Distinct().Count() != selectedSlots.Length)
        {
            return Reject(
                GearEnhancementStatus.DuplicateKitBagSlot,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "Gear, Attribute Stone, and catalyst must occupy distinct kit-bag slots.");
        }

        var equipment = KitBagSlots.GetItem(workingKitBag, request.Gear.KitBagSlot);
        var stoneItem = KitBagSlots.GetItem(workingKitBag, request.AttributeStone.KitBagSlot);
        var catalystItem = KitBagSlots.GetItem(workingKitBag, request.Catalyst.KitBagSlot);
        if (equipment.IsEmpty || stoneItem.IsEmpty || catalystItem.IsEmpty)
        {
            return Reject(
                GearEnhancementStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                equipment,
                "One or more selected gear-enhancement items are missing.");
        }

        if (equipment != request.Gear.ExpectedItem ||
            stoneItem != request.AttributeStone.ExpectedItem ||
            catalystItem != request.Catalyst.ExpectedItem)
        {
            return Reject(
                GearEnhancementStatus.StaleSelection,
                request.Operation,
                originalKitBag,
                equipment,
                "A selected gear-enhancement item changed after it was staged.");
        }

        if (equipment.Stack != 1)
        {
            return Reject(
                GearEnhancementStatus.InvalidEquipment,
                request.Operation,
                originalKitBag,
                equipment,
                "Gear must be a single non-stackable item.");
        }

        if (!templates.TryGet(equipment.Id, out var template) ||
            !EquipmentSlots.IsEquipmentKind(template.Kind) ||
            !EquipmentSlots.IsEquipmentSlot(template.EquipmentSlot) ||
            CreateEquipmentRule(template) is not { } equipmentRule ||
            equipmentRule.AllowedAttributeIds.Count == 0)
        {
            return Reject(
                GearEnhancementStatus.UnsupportedEquipment,
                request.Operation,
                originalKitBag,
                equipment,
                "The target is not supported equipment with a MainAttribute rule.");
        }

        if (!HasValidAttributeShape(equipment))
        {
            return Reject(
                GearEnhancementStatus.InvalidAttributeState,
                request.Operation,
                originalKitBag,
                equipment,
                "The target gear contains an invalid appended-attribute record.");
        }

        if (!templates.Materials.TryGetAttributeStone(stoneItem.Id, out var stone) ||
            stone.AllowedAttributeIds.Any(
                static attributeId =>
                    ElementalAttributeCatalog.IsElementalAttribute(attributeId)))
        {
            return Reject(
                GearEnhancementStatus.InvalidAttributeStone,
                request.Operation,
                originalKitBag,
                equipment,
                "The selected stone is not a supported ordinary Attribute Stone.");
        }

        if (!templates.Materials.TryGetGearEnhancement(catalystItem.Id, out var catalyst) ||
            !CatalystMatches(request.Operation, catalyst))
        {
            return Reject(
                GearEnhancementStatus.InvalidCatalyst,
                request.Operation,
                originalKitBag,
                equipment,
                "The selected catalyst does not match the requested operation.");
        }

        if (stoneItem.Stack < 1 || catalystItem.Stack < 1)
        {
            return Reject(
                GearEnhancementStatus.InsufficientMaterial,
                request.Operation,
                originalKitBag,
                equipment,
                "A selected gear-enhancement material stack is empty.");
        }

        if (!TryResolveChainAnchor(equipmentRule, stone, out var chainAnchorIndex))
        {
            return Reject(
                GearEnhancementStatus.AttributeNotAllowed,
                request.Operation,
                originalKitBag,
                equipment,
                "The target gear's MainAttribute rule does not allow this Attribute Stone.");
        }

        if (!TryMutateEquipment(
                request.Operation,
                equipment,
                stone,
                catalyst,
                chainAnchorIndex,
                out var equipmentAfter,
                out var mutationStatus,
                out var mutationReason))
        {
            return Reject(
                mutationStatus,
                request.Operation,
                originalKitBag,
                equipment,
                mutationReason);
        }

        var resultingBound = Math.Max(
            equipmentAfter.Bound,
            Math.Max(stoneItem.Bound, catalystItem.Bound));
        if (equipmentAfter.Bound != resultingBound)
        {
            equipmentAfter = equipmentAfter with { Bound = resultingBound };
        }

        var stoneAfter = ConsumeOne(stoneItem);
        var catalystAfter = ConsumeOne(catalystItem);
        var updatedKitBag = KitBagSlots.SetSlot(
            workingKitBag,
            request.Gear.KitBagSlot,
            equipmentAfter.ToCompactString());
        updatedKitBag = SetOrClear(updatedKitBag, request.AttributeStone.KitBagSlot, stoneAfter);
        updatedKitBag = SetOrClear(updatedKitBag, request.Catalyst.KitBagSlot, catalystAfter);

        return new GearEnhancementResult(
            GearEnhancementStatus.Succeeded,
            request.Operation,
            originalKitBag,
            updatedKitBag,
            equipment,
            equipmentAfter,
            [
                new GearEnhancementSlotMutation(request.Gear.KitBagSlot, equipment, equipmentAfter),
                new GearEnhancementSlotMutation(request.AttributeStone.KitBagSlot, stoneItem, stoneAfter),
                new GearEnhancementSlotMutation(request.Catalyst.KitBagSlot, catalystItem, catalystAfter)
            ]);
    }
}
