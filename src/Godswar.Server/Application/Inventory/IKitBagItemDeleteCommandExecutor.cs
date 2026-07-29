using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IKitBagItemDeleteCommandExecutor
{
    Task<KitBagItemDeleteExecutionResult> ExecuteAsync(
        CommandEnvelope<KitBagItemDeleteCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<KitBagItemDeleteExecutionResult> TryReplayAsync(
        CommandSubject subject,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
