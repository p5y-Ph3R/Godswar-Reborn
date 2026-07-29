namespace Godswar.Server.Infrastructure.Inventory;

internal enum PostgresKitBagItemDeleteCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    ItemDeleted = 3,
    InventoryRevisionAdvanced = 4,
    InventoryLedgerInserted = 5,
    OutboxInserted = 6,
    BeforeCommit = 7,
    AfterCommit = 8
}

internal interface IPostgresKitBagItemDeleteCommandProbe
{
    ValueTask ReachedAsync(
        PostgresKitBagItemDeleteCommandStage stage,
        int ordinal,
        CancellationToken cancellationToken);
}
