namespace Godswar.Server.Infrastructure.Inventory;

internal enum PostgresHolyStoneCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    TargetMutated = 3,
    StoneMutated = 4,
    OutputInserted = 5,
    InventoryRevisionAdvanced = 6,
    InventoryLedgerInserted = 7,
    OutboxInserted = 8,
    BeforeCommit = 9,
    AfterCommit = 10,
    WalletUpdated = 11,
    CurrencyLedgerInserted = 12
}

internal interface IPostgresHolyStoneCommandProbe
{
    ValueTask ReachedAsync(
        PostgresHolyStoneCommandStage stage,
        int ordinal,
        CancellationToken cancellationToken);
}
