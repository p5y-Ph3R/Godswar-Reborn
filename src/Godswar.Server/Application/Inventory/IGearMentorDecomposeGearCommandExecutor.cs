using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IGearMentorDecomposeGearCommandExecutor
{
    Task<GearMentorDecomposeGearExecutionResult> ExecuteAsync(
        CommandEnvelope<GearMentorDecomposeGearCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<GearMentorDecomposeGearExecutionResult> TryReplayAsync(
        CommandSubject subject,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
