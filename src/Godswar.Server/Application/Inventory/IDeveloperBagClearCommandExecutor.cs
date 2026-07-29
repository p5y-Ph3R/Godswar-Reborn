using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IDeveloperBagClearCommandExecutor
{
    Task<DeveloperBagClearExecutionResult> ExecuteAsync(
        CommandEnvelope<DeveloperBagClearCommand> envelope,
        CancellationToken cancellationToken = default);
}
