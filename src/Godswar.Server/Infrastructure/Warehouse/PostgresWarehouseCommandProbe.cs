namespace Godswar.Server.Infrastructure.Warehouse;

internal enum PostgresWarehouseCommandStage : byte
{
    TransferBeforeCommit = 1,
    ExpansionBeforeCommit = 2
}

internal interface IPostgresWarehouseCommandProbe
{
    ValueTask ReachedAsync(
        PostgresWarehouseCommandStage stage,
        CancellationToken cancellationToken);
}
