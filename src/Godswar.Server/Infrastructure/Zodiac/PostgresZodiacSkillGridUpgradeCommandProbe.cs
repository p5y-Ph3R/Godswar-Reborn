namespace Godswar.Server.Infrastructure.Zodiac;

internal enum PostgresZodiacSkillGridUpgradeCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    ResourcesUpdated = 3,
    GridUpdated = 4,
    OutboxInserted = 5,
    BeforeCommit = 6,
    AfterCommit = 7
}

internal interface IPostgresZodiacSkillGridUpgradeCommandProbe
{
    ValueTask ReachedAsync(
        PostgresZodiacSkillGridUpgradeCommandStage stage,
        CancellationToken cancellationToken);
}
