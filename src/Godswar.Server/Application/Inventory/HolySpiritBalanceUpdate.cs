namespace Godswar.Server.Application.Inventory;

internal sealed record HolySpiritBalanceUpdate(
    int CooledPhysicalReductionGradeOneMaximum,
    int CooledMagicReductionGradeOneMaximum,
    int CooledCriticalReductionGradeOneMaximum,
    long ExpectedRevision,
    string UpdatedBy)
{
    public void Validate()
    {
        var candidate = new HolySpiritBalanceSnapshot(
            CooledPhysicalReductionGradeOneMaximum,
            CooledMagicReductionGradeOneMaximum,
            CooledCriticalReductionGradeOneMaximum,
            ExpectedRevision,
            DateTimeOffset.UnixEpoch,
            UpdatedBy);
        candidate.Validate();
    }
}

internal enum HolySpiritBalanceUpdateStatus : byte
{
    Updated = 1,
    RevisionConflict = 2
}

internal sealed record HolySpiritBalanceUpdateResult(
    HolySpiritBalanceUpdateStatus Status,
    HolySpiritBalanceSnapshot Snapshot);
