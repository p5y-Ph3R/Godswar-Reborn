namespace Godswar.Server.Infrastructure.Inventory;

internal enum PostgresEquipmentBagTransferCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    CompatibilityAuditInserted = 3,
    ItemMoved = 4,
    InventoryRevisionAdvanced = 5,
    InventoryLedgerInserted = 6,
    OutboxInserted = 7,
    BeforeCommit = 8,
    AfterCommit = 9
}

internal interface IPostgresEquipmentBagTransferCommandProbe
{
    ValueTask ReachedAsync(
        PostgresEquipmentBagTransferCommandStage stage,
        int ordinal,
        CancellationToken cancellationToken);
}
