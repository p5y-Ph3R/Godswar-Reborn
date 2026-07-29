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

    public static int GetResultSubId(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status)
    {
        if (!IsReachable(operation, status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
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
                    HolyStoneCommandResultStatus.StaleTarget or
                    HolyStoneCommandResultStatus.TargetMissing,
            _ => false
        };

    public static bool IsSuccess(
        HolyStoneCommandResultStatus status) =>
        status is
            HolyStoneCommandResultStatus.Mounted or
            HolyStoneCommandResultStatus.Removed or
            HolyStoneCommandResultStatus.Drilled;
}
