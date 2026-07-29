namespace Godswar.Server.Infrastructure.Talents;

internal enum PostgresTalentUpgradeCommandStage : byte
{
    AuditInserted = 1,
    InboxInserted = 2,
    MutationApplied = 3,
    OutboxInserted = 4,
    BeforeCommit = 5,
    AfterCommit = 6
}

internal interface IPostgresTalentUpgradeCommandProbe
{
    ValueTask ReachedAsync(
        PostgresTalentUpgradeCommandStage stage,
        CancellationToken cancellationToken);
}
