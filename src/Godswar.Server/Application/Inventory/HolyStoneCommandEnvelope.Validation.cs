using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal static partial class HolyStoneCommandEnvelope
{
    public static CommandFamily Family(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Mount =>
                CommandFamily.HolyStoneMount,
            HolyStoneCommandOperation.Remove =>
                CommandFamily.HolyStoneRemove,
            HolyStoneCommandOperation.Drill =>
                CommandFamily.HolyStoneDrill,
            HolyStoneCommandOperation.AdvancedDrill =>
                CommandFamily.HolyStoneAdvancedDrill,
            HolyStoneCommandOperation.Upgrade =>
                CommandFamily.HolyStoneUpgrade,
            HolyStoneCommandOperation.Combine =>
                CommandFamily.HolyStoneCombine,
            HolyStoneCommandOperation.ImplementSpirit =>
                CommandFamily.HolyStoneImplementSpirit,
            HolyStoneCommandOperation.MountGearDrill =>
                CommandFamily.MountGearDrill,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static bool IsEndpoint(int npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is SpartaNpcId or AthensNpcId;

    public static bool AreEquivalentEndpoints(
        int firstNpcId,
        int firstDialogIndex,
        int secondNpcId,
        int secondDialogIndex) =>
        IsEndpoint(firstNpcId, firstDialogIndex) &&
        IsEndpoint(secondNpcId, secondDialogIndex);

    private static bool IsValidCommand(HolyStoneCommand command)
    {
        if (!IsValidIdentity(command.Identity) ||
            (command.Identity.IsRawLocalServer &&
             !SupportsRawLocalIdentity(command.Operation)) ||
            !Enum.IsDefined(command.Operation) ||
            !IsEndpoint(command.NpcId, command.DialogIndex) ||
            !Enum.IsDefined(command.TargetLocation) ||
            !IsValidTargetSlot(
                command.TargetLocation,
                command.TargetSlot) ||
            !TryGetStateBytes(
                command.ExpectedTargetCompactItemState,
                allowEmpty: true,
                out var targetStateBytes) ||
            !TryGetStateBytes(
                command.ExpectedStoneCompactItemState,
                allowEmpty: true,
                out var stoneStateBytes) ||
            !TryGetStateBytes(
                command.ExpectedCatalystCompactItemState,
                allowEmpty: true,
                out var catalystStateBytes) ||
            !TryGetStateBytes(
                command.ExpectedThirdMaterialCompactItemState,
                allowEmpty: true,
                out var thirdMaterialStateBytes) ||
            targetStateBytes.Length + stoneStateBytes.Length +
                catalystStateBytes.Length + thirdMaterialStateBytes.Length >
                MaximumCombinedStateBytes(command.Operation))
        {
            return false;
        }

        return command.Operation switch
        {
            HolyStoneCommandOperation.Mount =>
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command) &&
                (command.TargetLocation !=
                    HolyStoneTargetLocation.KitBag ||
                 command.TargetSlot != command.StoneKitBagSlot),
            HolyStoneCommandOperation.Remove =>
                command.SocketIndex is
                    >= MinimumSocketIndex and <= MaximumSocketIndex &&
                command.StoneKitBagSlot == NoStoneKitBagSlot &&
                command.ExpectedStoneCompactItemState == "[]" &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.Drill =>
                command.SocketIndex == ServerSelectedSocketIndex &&
                command.StoneKitBagSlot == NoStoneKitBagSlot &&
                command.ExpectedStoneCompactItemState == "[]" &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.MountGearDrill =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                command.StoneKitBagSlot == NoStoneKitBagSlot &&
                command.ExpectedStoneCompactItemState == "[]" &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.AdvancedDrill =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                command.TargetSlot != command.StoneKitBagSlot &&
                HasNoCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.Upgrade =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                command.TargetSlot != command.StoneKitBagSlot &&
                IsValidOptionalCatalyst(command) &&
                HasNoThirdMaterial(command),
            HolyStoneCommandOperation.Combine =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                IsKitBagSlot(command.CatalystKitBagSlot) &&
                IsKitBagSlot(command.ThirdMaterialKitBagSlot) &&
                command.ExpectedTargetCompactItemState != "[]" &&
                command.ExpectedStoneCompactItemState != "[]" &&
                command.ExpectedCatalystCompactItemState != "[]" &&
                command.ExpectedThirdMaterialCompactItemState != "[]" &&
                AreDistinctCombinationSlots(command),
            HolyStoneCommandOperation.ImplementSpirit =>
                command.TargetLocation == HolyStoneTargetLocation.KitBag &&
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                command.TargetSlot != command.StoneKitBagSlot &&
                IsValidOptionalCatalyst(command) &&
                HasNoThirdMaterial(command),
            _ => false
        };
    }

    private static bool IsValidTargetSlot(
        HolyStoneTargetLocation location,
        int slot) =>
        location switch
        {
            HolyStoneTargetLocation.Equipment =>
                slot == WeaponEquipmentSlot,
            HolyStoneTargetLocation.KitBag => IsKitBagSlot(slot),
            _ => false
        };

    private static bool IsKitBagSlot(int slot) =>
        slot is >= MinimumKitBagSlot and <= MaximumKitBagSlot;

    private static bool HasNoCatalyst(HolyStoneCommand command) =>
        command.CatalystKitBagSlot == NoStoneKitBagSlot &&
        command.ExpectedCatalystCompactItemState == "[]";

    private static bool HasNoThirdMaterial(HolyStoneCommand command) =>
        command.ThirdMaterialKitBagSlot == NoStoneKitBagSlot &&
        command.ExpectedThirdMaterialCompactItemState == "[]";

    private static bool IsValidOptionalCatalyst(
        HolyStoneCommand command) =>
        HasNoCatalyst(command) ||
        IsKitBagSlot(command.CatalystKitBagSlot) &&
        command.CatalystKitBagSlot != command.TargetSlot &&
        command.CatalystKitBagSlot != command.StoneKitBagSlot &&
        command.ExpectedCatalystCompactItemState != "[]";

    private static bool AreDistinctCombinationSlots(
        HolyStoneCommand command) =>
        command.TargetSlot != command.StoneKitBagSlot &&
        command.TargetSlot != command.CatalystKitBagSlot &&
        command.TargetSlot != command.ThirdMaterialKitBagSlot &&
        command.StoneKitBagSlot != command.CatalystKitBagSlot &&
        command.StoneKitBagSlot != command.ThirdMaterialKitBagSlot &&
        command.CatalystKitBagSlot != command.ThirdMaterialKitBagSlot;

    private static bool SupportsRawLocalIdentity(
        HolyStoneCommandOperation operation) =>
        operation is
            HolyStoneCommandOperation.Mount or
            HolyStoneCommandOperation.Remove or
            HolyStoneCommandOperation.Upgrade or
            HolyStoneCommandOperation.Combine or
            HolyStoneCommandOperation.ImplementSpirit or
            HolyStoneCommandOperation.MountGearDrill;

    private static int MaximumCombinedStateBytes(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.ImplementSpirit =>
                MaximumUpgradeCombinedStateUtf8Bytes,
            HolyStoneCommandOperation.Combine =>
                MaximumCombinationCombinedStateUtf8Bytes,
            _ => MaximumCombinedStateUtf8Bytes
        };
}
