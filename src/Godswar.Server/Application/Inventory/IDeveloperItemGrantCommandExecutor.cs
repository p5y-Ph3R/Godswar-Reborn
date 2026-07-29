using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

/// <summary>
/// Executes one authenticated, allowlisted developer material grant.
/// Implementations own the atomic inventory mutation, inbox result, audit
/// reference, and outbox append. Success is reported only after commit.
/// </summary>
internal interface IDeveloperItemGrantCommandExecutor
{
    Task<DeveloperItemGrantExecutionResult> ExecuteAsync(
        CommandEnvelope<DeveloperItemGrantCommand> envelope,
        CancellationToken cancellationToken = default);
}
