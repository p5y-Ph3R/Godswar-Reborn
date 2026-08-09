namespace Godswar.Server.Application.Inventory;

internal static class HolyStoneNativeResults
{
    public const int WrongSelectionSubId = 100;
    public const int TargetNotEquipmentSubId = 200;
    public const int StoneNotHolyStoneSubId = 300;
    public const int SocketNotDrilledSubId = 400;
    public const int StoneMissingSpiritSubId = 500;
    public const int SocketCapacityReachedSubId = 700;
    public const int MountedSubId = 800;
    public const int IncompatibleTargetSubId = 900;
    public const int InvalidSocketSubId = 1000;
    public const int BagFullSubId = 1100;
    public const int RemovedSubId = 1200;
    public const int MaximumSocketsSubId = 1300;
    public const int InsufficientFundsSubId = 1400;
    public const int DrilledSubId = 1500;
    public const int DuplicateSpiritSubId = 2200;
    public const int AdvancedSpellRequiredSubId = 2800;
    public const int AdvancedMaximumSocketsSubId = 2900;
    public const int DrillPrerequisiteSubId = 3000;
    public const int UpgradeTargetRequiredSubId = 1600;
    public const int ImplementTargetRequiredSubId = 1600;
    public const int EclipseStoneRequiredSubId = 1700;
    public const int MaximumStoneLevelSubId = 1800;
    public const int UpgradeSucceededSubId = 1900;
    public const int UpgradeFailedDowngradedSubId = 2000;
    public const int ImplementSpiritRequiredSubId = 2100;
    public const int UpgradeFailedProtectedSubId = 2300;
    public const int SignetMismatchSubId = 2400;
    public const int SignetProtectionUnavailableSubId = 3400;
    public const int TargetNotMountGearSubId = 3500;
    public const int CombinationSucceededSubId = 2500;
    public const int CombinationSelectionRequiredSubId = 2600;
    public const int CombinationNotAllowedSubId = 2700;
    public const int EclipseLevel1MissingSubId = 904002;
    public const int EclipseLevel2MissingSubId = 904102;
    public const int EclipseLevel3MissingSubId = 904202;

    public static int GetResultSubId(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status)
    {
        if (!IsReachable(operation, status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (operation == HolyStoneCommandOperation.Combine)
        {
            return status switch
            {
                HolyStoneCommandResultStatus.Combined =>
                    CombinationSucceededSubId,
                HolyStoneCommandResultStatus.CombinationNotAllowed or
                    HolyStoneCommandResultStatus.TargetNotHolyStone or
                    HolyStoneCommandResultStatus.MaximumStoneLevel =>
                    CombinationNotAllowedSubId,
                _ => CombinationSelectionRequiredSubId
            };
        }

        if (operation == HolyStoneCommandOperation.Upgrade)
        {
            return status switch
            {
                HolyStoneCommandResultStatus.Upgraded =>
                    UpgradeSucceededSubId,
                HolyStoneCommandResultStatus.UpgradeFailedDowngraded =>
                    UpgradeFailedDowngradedSubId,
                HolyStoneCommandResultStatus.UpgradeFailedProtected =>
                    UpgradeFailedProtectedSubId,
                HolyStoneCommandResultStatus.MaximumStoneLevel =>
                    MaximumStoneLevelSubId,
                HolyStoneCommandResultStatus.EclipseStoneRequired or
                    HolyStoneCommandResultStatus.StaleStone =>
                    EclipseStoneRequiredSubId,
                HolyStoneCommandResultStatus.EclipseLevel1Missing =>
                    EclipseLevel1MissingSubId,
                HolyStoneCommandResultStatus.EclipseLevel2Missing =>
                    EclipseLevel2MissingSubId,
                HolyStoneCommandResultStatus.EclipseLevel3Missing =>
                    EclipseLevel3MissingSubId,
                HolyStoneCommandResultStatus.SignetMismatch or
                    HolyStoneCommandResultStatus.CatalystMissing or
                    HolyStoneCommandResultStatus.StaleCatalyst =>
                    SignetMismatchSubId,
                HolyStoneCommandResultStatus
                    .SignetProtectionUnavailable =>
                    SignetProtectionUnavailableSubId,
                HolyStoneCommandResultStatus.TargetNotHolyStone or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.TargetMissing =>
                    UpgradeTargetRequiredSubId,
                _ => WrongSelectionSubId
            };
        }

        if (operation == HolyStoneCommandOperation.ImplementSpirit)
        {
            return status switch
            {
                HolyStoneCommandResultStatus.TargetNotHolyStone or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.TargetMissing =>
                    ImplementTargetRequiredSubId,
                HolyStoneCommandResultStatus.StoneNotHolyStone or
                    HolyStoneCommandResultStatus.StaleStone or
                    HolyStoneCommandResultStatus.StoneMissing =>
                    ImplementSpiritRequiredSubId,
                _ => WrongSelectionSubId
            };
        }

        if (operation == HolyStoneCommandOperation.AdvancedDrill)
        {
            return status switch
            {
                HolyStoneCommandResultStatus.Drilled => DrilledSubId,
                HolyStoneCommandResultStatus.StoneNotHolyStone or
                    HolyStoneCommandResultStatus.StaleStone or
                    HolyStoneCommandResultStatus.StoneMissing =>
                    AdvancedSpellRequiredSubId,
                HolyStoneCommandResultStatus.MaximumSockets =>
                    AdvancedMaximumSocketsSubId,
                _ => DrillPrerequisiteSubId
            };
        }

        if (operation == HolyStoneCommandOperation.Drill &&
            status == HolyStoneCommandResultStatus.DrillPrerequisite)
        {
            return DrillPrerequisiteSubId;
        }

        if (operation == HolyStoneCommandOperation.MountGearDrill &&
            status is HolyStoneCommandResultStatus.TargetNotEquipment or
                HolyStoneCommandResultStatus.TargetMissing)
        {
            return TargetNotMountGearSubId;
        }

        return status switch
        {
            HolyStoneCommandResultStatus.Mounted => MountedSubId,
            HolyStoneCommandResultStatus.Removed => RemovedSubId,
            HolyStoneCommandResultStatus.Drilled => DrilledSubId,
            HolyStoneCommandResultStatus.TargetNotEquipment or
                HolyStoneCommandResultStatus.TargetMissing =>
                TargetNotEquipmentSubId,
            HolyStoneCommandResultStatus.StoneNotHolyStone or
                HolyStoneCommandResultStatus.StaleStone or
                HolyStoneCommandResultStatus.StoneMissing =>
                StoneNotHolyStoneSubId,
            HolyStoneCommandResultStatus.SocketNotDrilled =>
                SocketNotDrilledSubId,
            HolyStoneCommandResultStatus.StoneMissingSpirit =>
                StoneMissingSpiritSubId,
            HolyStoneCommandResultStatus.SocketCapacityReached =>
                SocketCapacityReachedSubId,
            HolyStoneCommandResultStatus.IncompatibleTarget =>
                IncompatibleTargetSubId,
            HolyStoneCommandResultStatus.InvalidSocket or
                HolyStoneCommandResultStatus.SocketEmpty =>
                InvalidSocketSubId,
            HolyStoneCommandResultStatus.BagFull => BagFullSubId,
            HolyStoneCommandResultStatus.MaximumSockets =>
                MaximumSocketsSubId,
            HolyStoneCommandResultStatus.InsufficientFunds =>
                InsufficientFundsSubId,
            HolyStoneCommandResultStatus.DuplicateSpirit =>
                DuplicateSpiritSubId,
            HolyStoneCommandResultStatus.WrongSelection or
                HolyStoneCommandResultStatus.StaleTarget =>
                WrongSelectionSubId,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
    }

    public static bool IsReachable(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status) =>
        operation switch
        {
            HolyStoneCommandOperation.Mount =>
                status is
                    HolyStoneCommandResultStatus.Mounted or
                    HolyStoneCommandResultStatus.WrongSelection or
                    HolyStoneCommandResultStatus.TargetNotEquipment or
                    HolyStoneCommandResultStatus.StoneNotHolyStone or
                    HolyStoneCommandResultStatus.SocketNotDrilled or
                    HolyStoneCommandResultStatus.StoneMissingSpirit or
                    HolyStoneCommandResultStatus.SocketCapacityReached or
                    HolyStoneCommandResultStatus.IncompatibleTarget or
                    HolyStoneCommandResultStatus.DuplicateSpirit or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.StaleStone or
                    HolyStoneCommandResultStatus.TargetMissing or
                    HolyStoneCommandResultStatus.StoneMissing,
            HolyStoneCommandOperation.Remove =>
                status is
                    HolyStoneCommandResultStatus.Removed or
                    HolyStoneCommandResultStatus.WrongSelection or
                    HolyStoneCommandResultStatus.TargetNotEquipment or
                    HolyStoneCommandResultStatus.InvalidSocket or
                    HolyStoneCommandResultStatus.SocketEmpty or
                    HolyStoneCommandResultStatus.BagFull or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.TargetMissing,
            HolyStoneCommandOperation.Drill =>
                status is
                    HolyStoneCommandResultStatus.Drilled or
                    HolyStoneCommandResultStatus.WrongSelection or
                    HolyStoneCommandResultStatus.TargetNotEquipment or
                    HolyStoneCommandResultStatus.MaximumSockets or
                    HolyStoneCommandResultStatus.InsufficientFunds or
                    HolyStoneCommandResultStatus.DrillPrerequisite or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.TargetMissing,
            HolyStoneCommandOperation.MountGearDrill =>
                status is
                    HolyStoneCommandResultStatus.Drilled or
                    HolyStoneCommandResultStatus.WrongSelection or
                    HolyStoneCommandResultStatus.TargetNotEquipment or
                    HolyStoneCommandResultStatus.MaximumSockets or
                    HolyStoneCommandResultStatus.InsufficientFunds or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.TargetMissing,
            HolyStoneCommandOperation.AdvancedDrill =>
                status is
                    HolyStoneCommandResultStatus.Drilled or
                    HolyStoneCommandResultStatus.WrongSelection or
                    HolyStoneCommandResultStatus.TargetNotEquipment or
                    HolyStoneCommandResultStatus.StoneNotHolyStone or
                    HolyStoneCommandResultStatus.MaximumSockets or
                    HolyStoneCommandResultStatus.DrillPrerequisite or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.StaleStone or
                    HolyStoneCommandResultStatus.TargetMissing or
                    HolyStoneCommandResultStatus.StoneMissing,
            HolyStoneCommandOperation.Upgrade =>
                status is
                    HolyStoneCommandResultStatus.Upgraded or
                    HolyStoneCommandResultStatus.UpgradeFailedDowngraded or
                    HolyStoneCommandResultStatus.UpgradeFailedProtected or
                    HolyStoneCommandResultStatus.WrongSelection or
                    HolyStoneCommandResultStatus.TargetNotHolyStone or
                    HolyStoneCommandResultStatus.EclipseStoneRequired or
                    HolyStoneCommandResultStatus.MaximumStoneLevel or
                    HolyStoneCommandResultStatus.SignetMismatch or
                    HolyStoneCommandResultStatus
                        .SignetProtectionUnavailable or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.StaleStone or
                    HolyStoneCommandResultStatus.StaleCatalyst or
                    HolyStoneCommandResultStatus.TargetMissing or
                    HolyStoneCommandResultStatus.CatalystMissing or
                    HolyStoneCommandResultStatus.EclipseLevel1Missing or
                    HolyStoneCommandResultStatus.EclipseLevel2Missing or
                    HolyStoneCommandResultStatus.EclipseLevel3Missing,
            HolyStoneCommandOperation.Combine =>
                status is
                    HolyStoneCommandResultStatus.Combined or
                    HolyStoneCommandResultStatus
                        .CombinationSelectionRequired or
                    HolyStoneCommandResultStatus.CombinationNotAllowed or
                    HolyStoneCommandResultStatus.WrongSelection or
                    HolyStoneCommandResultStatus.TargetNotHolyStone or
                    HolyStoneCommandResultStatus.MaximumStoneLevel or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.StaleStone or
                    HolyStoneCommandResultStatus.StaleCatalyst or
                    HolyStoneCommandResultStatus.TargetMissing or
                    HolyStoneCommandResultStatus.StoneMissing or
                    HolyStoneCommandResultStatus.CatalystMissing,
            HolyStoneCommandOperation.ImplementSpirit =>
                status is
                    HolyStoneCommandResultStatus.SpiritImplemented or
                    HolyStoneCommandResultStatus.WrongSelection or
                    HolyStoneCommandResultStatus.TargetNotHolyStone or
                    HolyStoneCommandResultStatus.StoneNotHolyStone or
                    HolyStoneCommandResultStatus.IncompatibleTarget or
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.StaleStone or
                    HolyStoneCommandResultStatus.StaleCatalyst or
                    HolyStoneCommandResultStatus.TargetMissing or
                    HolyStoneCommandResultStatus.StoneMissing or
                    HolyStoneCommandResultStatus.CatalystMissing,
            _ => false
        };

    public static bool IsSuccess(
        HolyStoneCommandResultStatus status) =>
        status is
            HolyStoneCommandResultStatus.Mounted or
            HolyStoneCommandResultStatus.Removed or
            HolyStoneCommandResultStatus.Drilled or
            HolyStoneCommandResultStatus.Upgraded or
            HolyStoneCommandResultStatus.UpgradeFailedDowngraded or
            HolyStoneCommandResultStatus.UpgradeFailedProtected or
            HolyStoneCommandResultStatus.Combined or
            HolyStoneCommandResultStatus.SpiritImplemented;
}
