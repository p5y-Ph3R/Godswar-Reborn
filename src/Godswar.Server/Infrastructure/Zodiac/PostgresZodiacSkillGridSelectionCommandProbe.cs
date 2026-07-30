namespace Godswar.Server.Infrastructure.Zodiac;

internal enum PostgresZodiacSkillGridSelectionCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted,
    GridUpdated,
    OutboxInserted,
    BeforeCommit,
    AfterCommit
}

internal interface IPostgresZodiacSkillGridSelectionCommandProbe
{
    ValueTask ReachedAsync(
        PostgresZodiacSkillGridSelectionCommandStage stage,
        CancellationToken cancellationToken);
}
