using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresGearMentorMaterialConversionCommandExecutor
{
    private ClassSuitPlan CreateClassSuitPlan(
        string kitBag,
        byte profession,
        int playerLevel,
        ClassSuitCommand command)
    {
        if (command.Operation is
            ClassSuitCommandOperation.AddAttribute or
            ClassSuitCommandOperation.DeleteAttribute)
        {
            return CreateClassSuitAttributePlan(
                kitBag,
                profession,
                command);
        }

        var result = ClassSuitConversionPlanner.Create(
            _itemContent.Templates,
            kitBag,
            profession,
            playerLevel,
            new ClassSuitConversionRequest(
                ToConversionOperation(command.Operation),
                ToConversionSelection(command.Gear),
                command.PrimaryMaterial.HasValue
                    ? ToConversionSelection(
                        command.PrimaryMaterial.Value)
                    : null));
        return new ClassSuitPlan(
            MapClassSuitConversionStatus(result.Status),
            result.Mutations);
    }

    private static ClassSuitConversionOperation ToConversionOperation(
        ClassSuitCommandOperation operation) =>
        operation switch
        {
            ClassSuitCommandOperation.ExchangeTierI =>
                ClassSuitConversionOperation.ExchangeTierI,
            ClassSuitCommandOperation.ConvertToCommon =>
                ClassSuitConversionOperation.ConvertToCommon,
            ClassSuitCommandOperation.UpgradeTierII =>
                ClassSuitConversionOperation.UpgradeTierII,
            ClassSuitCommandOperation.UpgradeTierIII =>
                ClassSuitConversionOperation.UpgradeTierIII,
            ClassSuitCommandOperation.UpgradeTierIV =>
                ClassSuitConversionOperation.UpgradeTierIV,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static ClassSuitSlotSelection ToConversionSelection(
        ClassSuitCommandSelection selection) =>
        new(
            selection.KitBagSlot,
            CompactItemEntry.Parse(
                selection.ExpectedCompactItemState));

    private static ClassSuitCommandResultStatus
        MapClassSuitConversionStatus(ClassSuitConversionStatus status) =>
        status switch
        {
            ClassSuitConversionStatus.Succeeded =>
                ClassSuitCommandResultStatus.Succeeded,
            ClassSuitConversionStatus.SelectionMissing or
                ClassSuitConversionStatus.RequestMissing =>
                ClassSuitCommandResultStatus.SelectionMissing,
            ClassSuitConversionStatus.StaleSelection =>
                ClassSuitCommandResultStatus.StaleSelection,
            ClassSuitConversionStatus.InvalidEquipment or
                ClassSuitConversionStatus.InvalidKitBagSlot or
                ClassSuitConversionStatus.DuplicateKitBagSlot =>
                ClassSuitCommandResultStatus.InvalidEquipment,
            ClassSuitConversionStatus.UnsupportedSource or
                ClassSuitConversionStatus.UnsupportedOperation =>
                ClassSuitCommandResultStatus.UnsupportedSource,
            ClassSuitConversionStatus.ProfessionMismatch or
                ClassSuitConversionStatus.InvalidProfession =>
                ClassSuitCommandResultStatus.ProfessionMismatch,
            ClassSuitConversionStatus.UnsupportedReverseTier =>
                ClassSuitCommandResultStatus.UnsupportedReverseTier,
            ClassSuitConversionStatus.ContentMismatch =>
                ClassSuitCommandResultStatus.ContentMismatch,
            ClassSuitConversionStatus.PlayerLevelTooLow =>
                ClassSuitCommandResultStatus.PlayerLevelTooLow,
            ClassSuitConversionStatus.InvalidInsignia =>
                ClassSuitCommandResultStatus.InvalidMaterial,
            ClassSuitConversionStatus.InsufficientInsignia =>
                ClassSuitCommandResultStatus.InsufficientMaterial,
            ClassSuitConversionStatus.InsufficientCapacity =>
                ClassSuitCommandResultStatus.InsufficientCapacity,
            _ => throw new InvalidDataException(
                $"Unsupported Class Suit conversion status {status}.")
        };

    private ClassSuitPlan CreateClassSuitAttributePlan(
        string kitBag,
        byte profession,
        ClassSuitCommand command)
    {
        var result = ClassSuitAttributePlanner.Create(
            _itemContent.Templates,
            kitBag,
            profession,
            new ClassSuitAttributeRequest(
                command.Operation == ClassSuitCommandOperation.AddAttribute
                    ? ClassSuitAttributeOperation.AddClassSpecific
                    : ClassSuitAttributeOperation.DeleteClassSpecific,
                ToConversionSelection(command.Gear),
                ToConversionSelection(
                    command.PrimaryMaterial ??
                    throw new InvalidDataException(
                        "Class Suit attribute catalyst is missing.")),
                command.SecondaryMaterial.HasValue
                    ? ToConversionSelection(
                        command.SecondaryMaterial.Value)
                    : null));
        return new ClassSuitPlan(
            MapClassSuitAttributeStatus(result.Status),
            result.Mutations);
    }

    private static ClassSuitCommandResultStatus
        MapClassSuitAttributeStatus(ClassSuitAttributeStatus status) =>
        status switch
        {
            ClassSuitAttributeStatus.Succeeded =>
                ClassSuitCommandResultStatus.Succeeded,
            ClassSuitAttributeStatus.RequestMissing or
                ClassSuitAttributeStatus.SelectionMissing =>
                ClassSuitCommandResultStatus.SelectionMissing,
            ClassSuitAttributeStatus.StaleSelection =>
                ClassSuitCommandResultStatus.StaleSelection,
            ClassSuitAttributeStatus.InvalidWeapon or
                ClassSuitAttributeStatus.InvalidAttributeState or
                ClassSuitAttributeStatus.InvalidKitBagSlot or
                ClassSuitAttributeStatus.DuplicateKitBagSlot or
                ClassSuitAttributeStatus.UnsupportedOperation =>
                ClassSuitCommandResultStatus.InvalidEquipment,
            ClassSuitAttributeStatus.ProfessionMismatch or
                ClassSuitAttributeStatus.InvalidProfession =>
                ClassSuitCommandResultStatus.ProfessionMismatch,
            ClassSuitAttributeStatus.InvalidCatalyst or
                ClassSuitAttributeStatus.InvalidClassStone =>
                ClassSuitCommandResultStatus.InvalidMaterial,
            ClassSuitAttributeStatus.InsufficientMaterial =>
                ClassSuitCommandResultStatus.InsufficientMaterial,
            ClassSuitAttributeStatus.ClassAttributeAlreadyPresent =>
                ClassSuitCommandResultStatus.AttributeAlreadyPresent,
            ClassSuitAttributeStatus.AttributeSlotsFull =>
                ClassSuitCommandResultStatus.AttributeSlotsFull,
            ClassSuitAttributeStatus.ClassAttributeMissing =>
                ClassSuitCommandResultStatus.AttributeMissing,
            _ => throw new InvalidDataException(
                $"Unsupported Class Suit attribute status {status}.")
        };
}
