using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Talents;

/// <summary>
/// Executes one authenticated talent-upgrade intent. Implementations own the
/// atomic durable mutation, inbox result, audit reference, and outbox append.
/// Successful completion must occur only after that transaction commits.
/// </summary>
internal interface ITalentUpgradeCommandExecutor
{
    Task<TalentUpgradeExecutionResult> ExecuteAsync(
        CommandEnvelope<TalentUpgradeCommand> envelope,
        CancellationToken cancellationToken = default);
}
