namespace Godswar.Server.Infrastructure.Inventory;

internal enum PostgresEquipmentForgeCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    EquipmentMutated = 3,
    MaterialMutated = 4,
    WalletUpdated = 5,
    InventoryRevisionAdvanced = 6,
    CurrencyLedgerInserted = 7,
    InventoryLedgerInserted = 8,
    OutboxInserted = 9,
    BeforeCommit = 10,
    AfterCommit = 11
}

internal interface IPostgresEquipmentForgeCommandProbe
{
    ValueTask ReachedAsync(
        PostgresEquipmentForgeCommandStage stage,
        int ordinal,
        CancellationToken cancellationToken);
}
