namespace Godswar.Server.Application.Inventory;

/// <summary>
/// Stable wire intent used to decide whether a secure Class Suit retry is the
/// same operation. Item snapshots are deliberately excluded because a
/// successful first attempt changes those snapshots before a legitimate retry.
/// </summary>
internal readonly record struct ClassSuitReplayIntent(
    ClassSuitCommandOperation Operation,
    int NpcId,
    int DialogIndex,
    int GearKitBagSlot,
    int PrimaryMaterialKitBagSlot,
    int SecondaryMaterialKitBagSlot,
    ClassSuitItemLocation GearLocation = ClassSuitItemLocation.KitBag)
{
    public const int NoKitBagSlot = -1;

    public bool IsValid => IsValidIntent(this);

    public static bool TryCreate(
        ClassSuitCommandOperation operation,
        int npcId,
        int dialogIndex,
        int gearKitBagSlot,
        int primaryMaterialKitBagSlot,
        int secondaryMaterialKitBagSlot,
        out ClassSuitReplayIntent intent)
    {
        return TryCreate(
            operation,
            npcId,
            dialogIndex,
            ClassSuitItemLocation.KitBag,
            gearKitBagSlot,
            primaryMaterialKitBagSlot,
            secondaryMaterialKitBagSlot,
            out intent);
    }

    public static bool TryCreate(
        ClassSuitCommandOperation operation,
        int npcId,
        int dialogIndex,
        ClassSuitItemLocation gearLocation,
        int gearSlot,
        int primaryMaterialKitBagSlot,
        int secondaryMaterialKitBagSlot,
        out ClassSuitReplayIntent intent)
    {
        intent = new ClassSuitReplayIntent(
            operation,
            npcId,
            dialogIndex,
            gearSlot,
            primaryMaterialKitBagSlot,
            secondaryMaterialKitBagSlot,
            gearLocation);
        if (intent.IsValid)
        {
            return true;
        }

        intent = default;
        return false;
    }

    public static ClassSuitReplayIntent FromCommand(
        ClassSuitCommand command)
    {
        if (!TryCreate(
                command.Operation,
                command.NpcId,
                command.DialogIndex,
                command.Gear.Location,
                command.Gear.KitBagSlot,
                command.PrimaryMaterial?.KitBagSlot ?? NoKitBagSlot,
                command.SecondaryMaterial?.KitBagSlot ?? NoKitBagSlot,
                out var intent))
        {
            throw new ArgumentException(
                "The Class Suit command has no valid replay intent.",
                nameof(command));
        }

        return intent;
    }

    private static bool IsValidIntent(ClassSuitReplayIntent intent)
    {
        if (!Enum.IsDefined(intent.Operation) ||
            !ClassSuitCommandEnvelope.IsEndpoint(
                intent.NpcId,
                intent.DialogIndex) ||
            !Enum.IsDefined(intent.GearLocation) ||
            !IsGearSlot(intent.GearLocation, intent.GearKitBagSlot) ||
            intent.GearLocation == ClassSuitItemLocation.Equipment &&
            (intent.GearKitBagSlot !=
                ClassSuitCommandEnvelope.EquippedWeaponSlot ||
             intent.Operation is (
                 ClassSuitCommandOperation.AddAttribute or
                 ClassSuitCommandOperation.DeleteAttribute)))
        {
            return false;
        }

        var expectedMaterialCount = intent.Operation switch
        {
            ClassSuitCommandOperation.ConvertToCommon => 0,
            ClassSuitCommandOperation.AddAttribute => 2,
            _ => 1
        };
        var actualMaterialCount =
            (IsKitBagSlot(intent.PrimaryMaterialKitBagSlot) ? 1 : 0) +
            (IsKitBagSlot(intent.SecondaryMaterialKitBagSlot) ? 1 : 0);
        if (actualMaterialCount != expectedMaterialCount ||
            intent.PrimaryMaterialKitBagSlot == NoKitBagSlot &&
            intent.SecondaryMaterialKitBagSlot != NoKitBagSlot ||
            intent.PrimaryMaterialKitBagSlot != NoKitBagSlot &&
            !IsKitBagSlot(intent.PrimaryMaterialKitBagSlot) ||
            intent.SecondaryMaterialKitBagSlot != NoKitBagSlot &&
            !IsKitBagSlot(intent.SecondaryMaterialKitBagSlot))
        {
            return false;
        }

        var selectedMaterials = new[]
        {
            intent.PrimaryMaterialKitBagSlot,
            intent.SecondaryMaterialKitBagSlot
        }.Where(static slot => slot != NoKitBagSlot).ToArray();
        if (selectedMaterials.Distinct().Count() !=
            selectedMaterials.Length)
        {
            return false;
        }

        return intent.GearLocation != ClassSuitItemLocation.KitBag ||
            !selectedMaterials.Contains(intent.GearKitBagSlot);
    }

    private static bool IsGearSlot(
        ClassSuitItemLocation location,
        int slot) =>
        location switch
        {
            ClassSuitItemLocation.Equipment =>
                slot is >= ClassSuitCommandEnvelope.MinimumEquipmentSlot and
                    <= ClassSuitCommandEnvelope.MaximumEquipmentSlot,
            ClassSuitItemLocation.KitBag => IsKitBagSlot(slot),
            _ => false
        };

    private static bool IsKitBagSlot(int slot) =>
        slot is >= ClassSuitCommandEnvelope.MinimumKitBagSlot and
            <= ClassSuitCommandEnvelope.MaximumKitBagSlot;
}
