namespace Godswar.Server.Infrastructure.Zodiac;

internal enum PostgresZodiacSkillGridActivationCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    GridMutated = 3,
    WalletUpdated = 4,
    CurrencyLedgerInserted = 5,
    OutboxInserted = 6,
    BeforeCommit = 7,
    AfterCommit = 8
}

internal interface IPostgresZodiacSkillGridActivationCommandProbe
{
    ValueTask ReachedAsync(
        PostgresZodiacSkillGridActivationCommandStage stage,
        CancellationToken cancellationToken);
}
