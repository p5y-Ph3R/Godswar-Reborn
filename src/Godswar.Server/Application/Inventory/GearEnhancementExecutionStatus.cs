namespace Godswar.Server.Application.Inventory;

internal enum GearEnhancementCommandResultStatus : byte
{
    Succeeded = 1,
    SelectionMissing = 2,
    InvalidSelection = 3,
    StaleSelection = 4,
    InvalidEquipment = 5,
    UnsupportedEquipment = 6,
    InvalidAttributeState = 7,
    InvalidAttributeStone = 8,
    InvalidCatalyst = 9,
    InsufficientMaterial = 10,
    AttributeNotAllowed = 11,
    AttributeAlreadyPresent = 12,
    AttributeSlotsFull = 13,
    AttributeMissing = 14,
    AttributeAmbiguous = 15,
    AttributeNotEnhanceable = 16,
    AttributeLevelMismatch = 17,
    QuartzLevelMismatch = 18,
    AttributeMaximumLevel = 19
}

internal static class GearEnhancementNativeResults
{
    public const int SelectedItemMissingSubId = 1002;
    public const int MissingGearSubId = 1006;
    public const int MissingAttributeStoneSubId = 1007;
    public const int MissingQuartzSubId = 1008;
    public const int QuartzLevelMismatchSubId = 1009;
    public const int EnhanceSucceededSubId = 1010;
    public const int AttributeSlotsFullSubId = 1011;
    public const int AttributeAlreadyPresentSubId = 1012;
    public const int AddSucceededSubId = 1013;
    public const int AttributeNotAllowedSubId = 1018;
    public const int InvalidSelectionSubId = 1019;
    public const int MissingFlameSparkSubId = 1021;
    public const int MissingEnhanceAttributeSubId = 1023;
    public const int InsufficientEnhanceMaterialsSubId = 1026;
    public const int InsufficientAddMaterialsSubId = 1027;
    public const int MissingWaterGrainSubId = 1028;
    public const int MissingDeleteAttributeSubId = 1029;
    public const int DeleteSucceededSubId = 1030;
    public const int AttributeNotEnhanceableSubId = 1031;

    public static int GetResultSubId(
        GearEnhancementCommandOperation operation,
        GearEnhancementCommandResultStatus status) =>
        status switch
        {
            GearEnhancementCommandResultStatus.Succeeded =>
                operation switch
                {
                    GearEnhancementCommandOperation.Enhance =>
                        EnhanceSucceededSubId,
                    GearEnhancementCommandOperation.Add =>
                        AddSucceededSubId,
                    GearEnhancementCommandOperation.Delete =>
                        DeleteSucceededSubId,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(operation))
                },
            GearEnhancementCommandResultStatus.StaleSelection =>
                SelectedItemMissingSubId,
            GearEnhancementCommandResultStatus.InvalidEquipment or
                GearEnhancementCommandResultStatus.UnsupportedEquipment =>
                MissingGearSubId,
            GearEnhancementCommandResultStatus.InvalidAttributeStone =>
                MissingAttributeStoneSubId,
            GearEnhancementCommandResultStatus.InvalidCatalyst =>
                MissingCatalyst(operation),
            GearEnhancementCommandResultStatus.InsufficientMaterial =>
                operation switch
                {
                    GearEnhancementCommandOperation.Enhance =>
                        InsufficientEnhanceMaterialsSubId,
                    GearEnhancementCommandOperation.Add =>
                        InsufficientAddMaterialsSubId,
                    _ => InvalidSelectionSubId
                },
            GearEnhancementCommandResultStatus.AttributeNotAllowed =>
                AttributeNotAllowedSubId,
            GearEnhancementCommandResultStatus.AttributeAlreadyPresent =>
                AttributeAlreadyPresentSubId,
            GearEnhancementCommandResultStatus.AttributeSlotsFull =>
                AttributeSlotsFullSubId,
            GearEnhancementCommandResultStatus.AttributeMissing =>
                operation == GearEnhancementCommandOperation.Delete
                    ? MissingDeleteAttributeSubId
                    : operation == GearEnhancementCommandOperation.Enhance
                        ? MissingEnhanceAttributeSubId
                        : InvalidSelectionSubId,
            GearEnhancementCommandResultStatus.AttributeAmbiguous =>
                operation == GearEnhancementCommandOperation.Delete
                    ? MissingDeleteAttributeSubId
                    : operation == GearEnhancementCommandOperation.Enhance
                        ? AttributeNotEnhanceableSubId
                        : InvalidSelectionSubId,
            GearEnhancementCommandResultStatus.AttributeNotEnhanceable or
                GearEnhancementCommandResultStatus.AttributeMaximumLevel =>
                AttributeNotEnhanceableSubId,
            GearEnhancementCommandResultStatus.AttributeLevelMismatch or
                GearEnhancementCommandResultStatus.QuartzLevelMismatch =>
                QuartzLevelMismatchSubId,
            _ => InvalidSelectionSubId
        };

    public static bool IsReachable(
        GearEnhancementCommandOperation operation,
        GearEnhancementCommandResultStatus status) =>
        operation switch
        {
            GearEnhancementCommandOperation.Add =>
                status is not (
                    GearEnhancementCommandResultStatus.AttributeMissing or
                    GearEnhancementCommandResultStatus.AttributeAmbiguous or
                    GearEnhancementCommandResultStatus
                        .AttributeNotEnhanceable or
                    GearEnhancementCommandResultStatus
                        .AttributeLevelMismatch or
                    GearEnhancementCommandResultStatus.QuartzLevelMismatch or
                    GearEnhancementCommandResultStatus
                        .AttributeMaximumLevel),
            GearEnhancementCommandOperation.Enhance =>
                status is not (
                    GearEnhancementCommandResultStatus
                        .AttributeAlreadyPresent or
                    GearEnhancementCommandResultStatus.AttributeSlotsFull),
            GearEnhancementCommandOperation.Delete =>
                status is not (
                    GearEnhancementCommandResultStatus
                        .AttributeAlreadyPresent or
                    GearEnhancementCommandResultStatus.AttributeSlotsFull or
                    GearEnhancementCommandResultStatus
                        .AttributeNotEnhanceable or
                    GearEnhancementCommandResultStatus
                        .AttributeLevelMismatch or
                    GearEnhancementCommandResultStatus.QuartzLevelMismatch or
                    GearEnhancementCommandResultStatus
                        .AttributeMaximumLevel),
            _ => false
        };

    private static int MissingCatalyst(
        GearEnhancementCommandOperation operation) =>
        operation switch
        {
            GearEnhancementCommandOperation.Enhance =>
                MissingQuartzSubId,
            GearEnhancementCommandOperation.Add =>
                MissingFlameSparkSubId,
            GearEnhancementCommandOperation.Delete =>
                MissingWaterGrainSubId,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
}
