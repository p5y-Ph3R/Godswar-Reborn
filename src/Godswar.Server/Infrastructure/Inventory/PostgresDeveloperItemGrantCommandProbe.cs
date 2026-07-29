namespace Godswar.Server.Infrastructure.Inventory;

internal enum PostgresDeveloperItemGrantCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    InventoryMutated = 3,
    LedgerInserted = 4,
    OutboxInserted = 5,
    BeforeCommit = 6,
    AfterCommit = 7
}

internal interface IPostgresDeveloperItemGrantCommandProbe
{
    ValueTask ReachedAsync(
        PostgresDeveloperItemGrantCommandStage stage,
        CancellationToken cancellationToken);
}
