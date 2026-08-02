namespace Godswar.Server.Application.Inventory;

internal static class HolySuitNativeResults
{
    public const int WrongSelectionSubId = 100;
    public const int InsufficientExperienceSubId = 200;
    public const int NotHolyBoxSubId = 300;
    public const int ExperienceStoredSubId = 400;
    public const int TransferWrongSelectionSubId = 500;
    public const int TargetNotEquipmentSubId = 600;
    public const int HolyBoxEmptySubId = 700;
    public const int ExperienceTransferredSubId = 800;
    public const int EquipmentExperienceLimitSubId = 900;
    public const int EquipmentInsufficientExperienceSubId = 1000;
    public const int MaximumHolySuitSubId = 1100;
    public const int WareNotRequiredSubId = 1200;
    public const int WareConsumedSubId = 1300;
    public const int WareUpgradeFailedSubId = 1400;
    public const int WareTypeMismatchSubId = 1500;
    public const int InsufficientWaresSubId = 1600;
    public const int SecondItemNotHolyBoxSubId = 1700;
    public const int HolyBoxFullSubId = 1800;
    public const int RequestedExperienceLimitSubId = 1900;
    public const int DailyStoreLimitSubId = 2000;
    public const int ExperienceTransformedSubId = 2100;
    public const int InsufficientFundsSubId = 2200;
    public const int BagFullSubId = 2300;
    public const int StoreLevelRequirementSubId = 2101;

    public static int GetResultSubId(
        HolySuitCommandOperation operation,
        HolySuitCommandResultStatus status)
    {
        if (!IsReachable(operation, status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return status switch
        {
            HolySuitCommandResultStatus.ExperienceStored =>
                ExperienceStoredSubId,
            HolySuitCommandResultStatus.ExperienceTransferred =>
                ExperienceTransferredSubId,
            HolySuitCommandResultStatus.WareConsumed =>
                WareConsumedSubId,
            HolySuitCommandResultStatus.ExperienceTransformed =>
                ExperienceTransformedSubId,
            HolySuitCommandResultStatus.InsufficientCharacterExperience =>
                InsufficientExperienceSubId,
            HolySuitCommandResultStatus.NotHolyBox or
                HolySuitCommandResultStatus.PrimaryItemMissing
                    when operation ==
                        HolySuitCommandOperation.StoreExperience =>
                NotHolyBoxSubId,
            HolySuitCommandResultStatus.HolyBoxFull =>
                HolyBoxFullSubId,
            HolySuitCommandResultStatus.DailyStoreLimitExceeded =>
                DailyStoreLimitSubId,
            HolySuitCommandResultStatus.TargetNotEquipment or
                HolySuitCommandResultStatus.PrimaryItemMissing =>
                TargetNotEquipmentSubId,
            HolySuitCommandResultStatus.HolyBoxEmpty =>
                HolyBoxEmptySubId,
            HolySuitCommandResultStatus.EquipmentExperienceLimitReached =>
                EquipmentExperienceLimitSubId,
            HolySuitCommandResultStatus.EquipmentInsufficientExperience =>
                EquipmentInsufficientExperienceSubId,
            HolySuitCommandResultStatus.MaximumHolySuit =>
                MaximumHolySuitSubId,
            HolySuitCommandResultStatus.WareNotRequired or
                HolySuitCommandResultStatus.SecondaryItemMissing
                    when operation == HolySuitCommandOperation.ConsumeWare =>
                WareNotRequiredSubId,
            HolySuitCommandResultStatus.WareUpgradeFailedRoll =>
                WareUpgradeFailedSubId,
            HolySuitCommandResultStatus.WareTypeMismatch =>
                WareTypeMismatchSubId,
            HolySuitCommandResultStatus.InsufficientWares or
                HolySuitCommandResultStatus.InsufficientPrisms =>
                InsufficientWaresSubId,
            HolySuitCommandResultStatus.SecondItemNotHolyBox or
                HolySuitCommandResultStatus.SecondaryItemMissing =>
                SecondItemNotHolyBoxSubId,
            HolySuitCommandResultStatus.RequestedExperienceLimitExceeded =>
                RequestedExperienceLimitSubId,
            HolySuitCommandResultStatus.InsufficientFunds =>
                InsufficientFundsSubId,
            HolySuitCommandResultStatus.BagFull =>
                BagFullSubId,
            HolySuitCommandResultStatus.LevelRequirementNotMet =>
                StoreLevelRequirementSubId,
            HolySuitCommandResultStatus.WrongSelection or
                HolySuitCommandResultStatus.StalePrimaryItem or
                HolySuitCommandResultStatus.StaleSecondaryItem =>
                operation == HolySuitCommandOperation.StoreExperience ||
                operation ==
                    HolySuitCommandOperation.TransformExperience
                    ? WrongSelectionSubId
                    : TransferWrongSelectionSubId,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
    }

    public static bool IsReachable(
        HolySuitCommandOperation operation,
        HolySuitCommandResultStatus status) =>
        operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
                status is
                    HolySuitCommandResultStatus.ExperienceStored or
                    HolySuitCommandResultStatus.WrongSelection or
                    HolySuitCommandResultStatus
                        .InsufficientCharacterExperience or
                    HolySuitCommandResultStatus.NotHolyBox or
                    HolySuitCommandResultStatus.HolyBoxFull or
                    HolySuitCommandResultStatus
                        .DailyStoreLimitExceeded or
                    HolySuitCommandResultStatus
                        .RequestedExperienceLimitExceeded or
                    HolySuitCommandResultStatus.StalePrimaryItem or
                    HolySuitCommandResultStatus.PrimaryItemMissing or
                    HolySuitCommandResultStatus.LevelRequirementNotMet,
            HolySuitCommandOperation.TransferExperience =>
                status is
                    HolySuitCommandResultStatus.ExperienceTransferred or
                    HolySuitCommandResultStatus.WrongSelection or
                    HolySuitCommandResultStatus.TargetNotEquipment or
                    HolySuitCommandResultStatus.HolyBoxEmpty or
                    HolySuitCommandResultStatus
                        .EquipmentExperienceLimitReached or
                    HolySuitCommandResultStatus.MaximumHolySuit or
                    HolySuitCommandResultStatus.SecondItemNotHolyBox or
                    HolySuitCommandResultStatus.StalePrimaryItem or
                    HolySuitCommandResultStatus.StaleSecondaryItem or
                    HolySuitCommandResultStatus.PrimaryItemMissing or
                    HolySuitCommandResultStatus.SecondaryItemMissing or
                    HolySuitCommandResultStatus.LevelRequirementNotMet,
            HolySuitCommandOperation.ConsumeWare =>
                status is
                    HolySuitCommandResultStatus.WareConsumed or
                    HolySuitCommandResultStatus.WrongSelection or
                    HolySuitCommandResultStatus.TargetNotEquipment or
                    HolySuitCommandResultStatus
                        .EquipmentInsufficientExperience or
                    HolySuitCommandResultStatus.MaximumHolySuit or
                    HolySuitCommandResultStatus.WareNotRequired or
                    HolySuitCommandResultStatus.WareUpgradeFailedRoll or
                    HolySuitCommandResultStatus.WareTypeMismatch or
                    HolySuitCommandResultStatus.InsufficientWares or
                    HolySuitCommandResultStatus.InsufficientPrisms or
                    HolySuitCommandResultStatus.StalePrimaryItem or
                    HolySuitCommandResultStatus.StaleSecondaryItem or
                    HolySuitCommandResultStatus.PrimaryItemMissing or
                    HolySuitCommandResultStatus.SecondaryItemMissing or
                    HolySuitCommandResultStatus.LevelRequirementNotMet,
            HolySuitCommandOperation.TransformExperience =>
                status is
                    HolySuitCommandResultStatus.ExperienceTransformed or
                    HolySuitCommandResultStatus.WrongSelection or
                    HolySuitCommandResultStatus
                        .InsufficientCharacterExperience or
                    HolySuitCommandResultStatus.InsufficientFunds or
                    HolySuitCommandResultStatus.BagFull or
                    HolySuitCommandResultStatus.LevelRequirementNotMet,
            _ => false
        };

    public static bool IsCommitted(
        HolySuitCommandResultStatus status) =>
        IsSuccess(status) ||
        status == HolySuitCommandResultStatus.WareUpgradeFailedRoll;

    public static bool IsSuccess(
        HolySuitCommandResultStatus status) =>
        status is
            HolySuitCommandResultStatus.ExperienceStored or
            HolySuitCommandResultStatus.ExperienceTransferred or
            HolySuitCommandResultStatus.WareConsumed or
            HolySuitCommandResultStatus.ExperienceTransformed;
}
