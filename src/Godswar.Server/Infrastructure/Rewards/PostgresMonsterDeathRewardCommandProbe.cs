namespace Godswar.Server.Infrastructure.Rewards;

internal enum PostgresMonsterDeathRewardCommandStage : byte
{
    DeathIdentityLocked = 1,
    AuditInserted = 2,
    InboxInserted = 3,
    ProgressionUpdated = 4,
    OutboxInserted = 5,
    SettlementInserted = 6,
    BeforeCommit = 7,
    AfterCommit = 8
}

internal interface IPostgresMonsterDeathRewardCommandProbe
{
    ValueTask ReachedAsync(
        PostgresMonsterDeathRewardCommandStage stage,
        CancellationToken cancellationToken);
}
