namespace Godswar.Server.Infrastructure.Inventory;

internal enum PostgresKitBagItemMoveCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    SourceMovedToTemporarySlot = 3,
    DestinationMovedToSourceSlot = 4,
    SourceMovedToDestinationSlot = 5,
    CompatibilityAuditInserted = 6,
    InventoryRevisionAdvanced = 7,
    InventoryLedgerInserted = 8,
    OutboxInserted = 9,
    BeforeCommit = 10,
    AfterCommit = 11
}

internal interface IPostgresKitBagItemMoveCommandProbe
{
    ValueTask ReachedAsync(
        PostgresKitBagItemMoveCommandStage stage,
        int ordinal,
        CancellationToken cancellationToken);
}
