namespace Godswar.Server.Infrastructure.Inventory;

internal enum PostgresGearEnhancementCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    GearMutated = 3,
    CatalystMutated = 4,
    AttributeStoneMutated = 5,
    InventoryRevisionAdvanced = 6,
    LedgerInserted = 7,
    OutboxInserted = 8,
    BeforeCommit = 9,
    AfterCommit = 10
}

internal interface IPostgresGearEnhancementCommandProbe
{
    ValueTask ReachedAsync(
        PostgresGearEnhancementCommandStage stage,
        CancellationToken cancellationToken);
}
