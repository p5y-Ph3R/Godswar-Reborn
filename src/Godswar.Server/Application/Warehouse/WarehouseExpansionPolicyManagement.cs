namespace Godswar.Server.Application.Warehouse;

internal sealed record WarehouseExpansionPolicyUpdate(
    long ExpectedRevision,
    string UpdatedBy,
    IReadOnlyList<WarehouseExpansionPolicyLevel> Levels);

internal enum WarehouseExpansionPolicyUpdateStatus : byte
{
    Updated = 1,
    Unchanged = 2,
    RevisionConflict = 3,
    Invalid = 4
}

internal sealed record WarehouseExpansionPolicyUpdateResult(
    WarehouseExpansionPolicyUpdateStatus Status,
    WarehouseExpansionPolicySnapshot? Snapshot);

internal interface IWarehouseExpansionPolicySettingsStore
{
    Task<WarehouseExpansionPolicyUpdateResult> TryPublishSuccessorAsync(
        WarehouseExpansionPolicyUpdate update,
        CancellationToken cancellationToken = default);
}
