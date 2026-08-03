using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// Authoritative class-specific weapon-attribute rules used by dialogue 37.
/// It plans every bag change before a persistence executor commits anything.
/// </summary>
internal static partial class ClassSuitAttributePlanner
{
    private const int KitBagSlotCount = 96;

    private static readonly IReadOnlyDictionary<uint, (byte ProfessionGroup, int AttributeId)>
        ClassStones = new Dictionary<uint, (byte, int)>
        {
            [9950] = (0, 200),
            [9951] = (0, 201),
            [9952] = (0, 210),
            [9953] = (0, 211),
            [9954] = (1, 220),
            [9955] = (1, 221),
            [9956] = (1, 230),
            [9957] = (1, 231)
        };

    private static readonly IReadOnlySet<int> AllClassAttributeIds =
        ClassStones.Values.Select(static value => value.AttributeId).ToHashSet();

    public static ClassSuitAttributeResult Create(
        IItemTemplateCatalog templates,
        string kitBag,
        byte profession,
        ClassSuitAttributeRequest? request)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var originalKitBag = kitBag ?? string.Empty;
        var normalizedKitBag = string.IsNullOrWhiteSpace(kitBag)
            ? GameDefaults.EmptyKitBag
            : kitBag;
        if (request is null)
        {
            return Reject(
                ClassSuitAttributeStatus.RequestMissing,
                null,
                originalKitBag,
                CompactItemEntry.Empty,
                "Class-specific attribute request was missing.");
        }

        if (!Enum.IsDefined(request.Operation))
        {
            return Reject(
                ClassSuitAttributeStatus.UnsupportedOperation,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "Class-specific attribute operation is not supported.");
        }

        if (profession > 3)
        {
            return Reject(
                ClassSuitAttributeStatus.InvalidProfession,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "The authoritative character profession is invalid.");
        }

        if (request.Gear is null || request.Catalyst is null ||
            (request.Operation == ClassSuitAttributeOperation.AddClassSpecific) !=
            (request.ClassStone is not null))
        {
            return Reject(
                ClassSuitAttributeStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                request.Operation == ClassSuitAttributeOperation.AddClassSpecific
                    ? "Select a Class Suit weapon, Flame Spark, and class-specific stone."
                    : "Select a Class Suit weapon and Water Grain only.");
        }

        var selections = request.ClassStone is null
            ? new[] { request.Gear, request.Catalyst }
            : new[] { request.Gear, request.Catalyst, request.ClassStone };
        var slots = selections.Select(static value => value.KitBagSlot).ToArray();
        if (slots.Any(static slot => slot is < 0 or >= KitBagSlotCount))
        {
            return Reject(
                ClassSuitAttributeStatus.InvalidKitBagSlot,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "A class-specific attribute selection used an invalid kit-bag slot.");
        }

        if (slots.Distinct().Count() != slots.Length)
        {
            return Reject(
                ClassSuitAttributeStatus.DuplicateKitBagSlot,
                request.Operation,
                originalKitBag,
                CompactItemEntry.Empty,
                "Gear, catalyst, and stone must occupy distinct kit-bag slots.");
        }

        var before = Enumerable.Range(0, KitBagSlotCount)
            .Select(slot => KitBagSlots.GetItem(normalizedKitBag, slot))
            .ToArray();
        foreach (var selection in selections)
        {
            var current = before[selection.KitBagSlot];
            if (current.IsEmpty)
            {
                return Reject(
                    ClassSuitAttributeStatus.SelectionMissing,
                    request.Operation,
                    originalKitBag,
                    before[request.Gear.KitBagSlot],
                    "A selected class-specific attribute item is no longer in the bag.");
            }

            if (current != selection.ExpectedItem)
            {
                return Reject(
                    ClassSuitAttributeStatus.StaleSelection,
                    request.Operation,
                    originalKitBag,
                    before[request.Gear.KitBagSlot],
                    "A selected class-specific attribute item changed after it was staged.");
            }
        }

        var equipment = before[request.Gear.KitBagSlot];
        if (!TryValidateWeapon(
                templates,
                equipment,
                profession,
                out var weaponTier,
                out var weaponStatus,
                out var weaponReason))
        {
            return Reject(
                weaponStatus,
                request.Operation,
                originalKitBag,
                equipment,
                weaponReason);
        }

        if (request.Operation ==
                ClassSuitAttributeOperation.AddClassSpecific &&
            weaponTier is ClassSuitTier.TierI or ClassSuitTier.TierII)
        {
            return Reject(
                ClassSuitAttributeStatus.InvalidWeapon,
                request.Operation,
                originalKitBag,
                equipment,
                "Class-specific stones require a Class Suit III or IV weapon.");
        }

        var catalyst = before[request.Catalyst.KitBagSlot];
        var expectedCatalystKind = request.Operation ==
            ClassSuitAttributeOperation.AddClassSpecific
            ? GearEnhancementMaterialKind.FlameSpark
            : GearEnhancementMaterialKind.WaterGrain;
        if (!templates.Materials.TryGetGearEnhancement(
                catalyst.Id,
                out var catalystDefinition) ||
            catalystDefinition.Kind != expectedCatalystKind)
        {
            return Reject(
                ClassSuitAttributeStatus.InvalidCatalyst,
                request.Operation,
                originalKitBag,
                equipment,
                request.Operation == ClassSuitAttributeOperation.AddClassSpecific
                    ? "A Flame Spark is required to add a class-specific stat."
                    : "A Water Grain is required to delete a class-specific stat.");
        }

        if (catalyst.Stack < 1)
        {
            return Reject(
                ClassSuitAttributeStatus.InsufficientMaterial,
                request.Operation,
                originalKitBag,
                equipment,
                "The selected catalyst stack is empty.");
        }

        var working = before.ToArray();
        var materials = new List<ClassSuitMaterialChange>();
        CompactItemEntry equipmentAfter;
        if (request.Operation == ClassSuitAttributeOperation.AddClassSpecific)
        {
            var stoneSelection = request.ClassStone!;
            var stone = before[stoneSelection.KitBagSlot];
            if (!TryResolveStone(
                    templates.Materials,
                    stone.Id,
                    profession,
                    out var attributeId))
            {
                return Reject(
                    ClassSuitAttributeStatus.InvalidClassStone,
                    request.Operation,
                    originalKitBag,
                    equipment,
                    "The selected class-specific stone does not belong to this profession.");
            }

            if (stone.Stack < 1)
            {
                return Reject(
                    ClassSuitAttributeStatus.InsufficientMaterial,
                    request.Operation,
                    originalKitBag,
                    equipment,
                    "The selected class-specific stone stack is empty.");
            }

            if (!TryAddAttribute(
                    equipment,
                    attributeId,
                    weaponTier,
                    out equipmentAfter,
                    out var attributeStatus,
                    out var attributeReason))
            {
                return Reject(
                    attributeStatus,
                    request.Operation,
                    originalKitBag,
                    equipment,
                    attributeReason);
            }

            equipmentAfter = equipmentAfter with
            {
                Bound = Math.Max(
                    equipmentAfter.Bound,
                    Math.Max(catalyst.Bound, stone.Bound))
            };
            working[stoneSelection.KitBagSlot] = ConsumeOne(stone);
            materials.Add(new ClassSuitMaterialChange(
                stone.Id,
                1,
                stone.Bound,
                ClassSuitMaterialDirection.Consumed));
        }
        else if (!TryDeleteAttribute(
                     equipment,
                     profession,
                     out equipmentAfter,
                     out var attributeStatus,
                     out var attributeReason))
        {
            return Reject(
                attributeStatus,
                request.Operation,
                originalKitBag,
                equipment,
                attributeReason);
        }

        equipmentAfter = equipmentAfter with
        {
            Bound = Math.Max(equipmentAfter.Bound, catalyst.Bound)
        };
        working[request.Gear.KitBagSlot] = equipmentAfter;
        working[request.Catalyst.KitBagSlot] = ConsumeOne(catalyst);
        materials.Add(new ClassSuitMaterialChange(
            catalyst.Id,
            1,
            catalyst.Bound,
            ClassSuitMaterialDirection.Consumed));

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

        return new ClassSuitAttributeResult(
            ClassSuitAttributeStatus.Succeeded,
            request.Operation,
            originalKitBag,
            updatedKitBag,
            equipment,
            equipmentAfter,
            mutations,
            materials);
    }

    private static bool TryValidateWeapon(
        IItemTemplateCatalog templates,
        CompactItemEntry equipment,
        byte profession,
        out ClassSuitTier tier,
        out ClassSuitAttributeStatus status,
        out string reason)
    {
        tier = default;
        if (equipment.Stack != 1 ||
            !templates.TryGet(equipment.Id, out var template) ||
            !template.Kind.Equals("weapon", StringComparison.OrdinalIgnoreCase))
        {
            status = ClassSuitAttributeStatus.InvalidWeapon;
            reason = "Only one genuine Class Suit weapon can receive a class-specific stat.";
            return false;
        }

        if (!ClassSuitConversionCatalog.TryResolveSuit(
                profession,
                equipment.Id,
                out _,
                out tier))
        {
            var belongsToAnotherProfession = Enumerable.Range(0, 4)
                .Where(value => value != profession)
                .Any(value => ClassSuitConversionCatalog.TryResolveSuit(
                    checked((byte)value),
                    equipment.Id,
                    out _,
                    out _));
            status = belongsToAnotherProfession
                ? ClassSuitAttributeStatus.ProfessionMismatch
                : ClassSuitAttributeStatus.InvalidWeapon;
            reason = belongsToAnotherProfession
                ? "This Class Suit weapon belongs to a different profession."
                : "Only a Class Suit weapon can receive a class-specific stat.";
            return false;
        }

        status = ClassSuitAttributeStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool TryResolveStone(
        IItemMaterialCatalog materials,
        uint itemId,
        byte profession,
        out int attributeId)
    {
        attributeId = 0;
        return ClassStones.TryGetValue(itemId, out var rule) &&
            IsProfessionAllowed(rule.ProfessionGroup, profession) &&
            materials.TryGetAttributeStone(itemId, out var stone) &&
            stone.AllowedAttributeIds.Count == 1 &&
            stone.AllowedAttributeIds[0] == rule.AttributeId &&
            (attributeId = rule.AttributeId) > 0;
    }

    private static bool IsProfessionAllowed(
        byte professionGroup,
        byte profession) =>
        professionGroup switch
        {
            0 => profession is 0 or 1,
            1 => profession is 2 or 3,
            _ => false
        };

    private static CompactItemEntry ConsumeOne(CompactItemEntry item) =>
        item.Stack == 1
            ? CompactItemEntry.Empty
            : item with { Stack = checked((short)(item.Stack - 1)) };

    private static ClassSuitAttributeResult Reject(
        ClassSuitAttributeStatus status,
        ClassSuitAttributeOperation? operation,
        string originalKitBag,
        CompactItemEntry equipment,
        string reason) =>
        new(
            status,
            operation,
            originalKitBag,
            originalKitBag,
            equipment,
            equipment,
            [],
            [],
            reason);
}
