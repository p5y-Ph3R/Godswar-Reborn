using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IGearEnhancementCommandExecutor
{
    Task<GearEnhancementExecutionResult> ExecuteAsync(
        CommandEnvelope<GearEnhancementCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<GearEnhancementExecutionResult> TryReplayAsync(
        CommandSubject subject,
        GearEnhancementCommandOperation operation,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
