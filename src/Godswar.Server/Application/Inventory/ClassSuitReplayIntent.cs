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
    int SecondaryMaterialKitBagSlot)
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
        intent = new ClassSuitReplayIntent(
            operation,
            npcId,
            dialogIndex,
            gearKitBagSlot,
            primaryMaterialKitBagSlot,
            secondaryMaterialKitBagSlot);
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
            !IsKitBagSlot(intent.GearKitBagSlot))
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

        var selected = new[]
        {
            intent.GearKitBagSlot,
            intent.PrimaryMaterialKitBagSlot,
            intent.SecondaryMaterialKitBagSlot
        }.Where(static slot => slot != NoKitBagSlot).ToArray();
        return selected.Distinct().Count() == selected.Length;
    }

    private static bool IsKitBagSlot(int slot) =>
        slot is >= ClassSuitCommandEnvelope.MinimumKitBagSlot and
            <= ClassSuitCommandEnvelope.MaximumKitBagSlot;
}
