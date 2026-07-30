using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IKitBagItemDeleteCommandExecutor
{
    Task<KitBagItemDeleteExecutionResult> ExecuteAsync(
        CommandEnvelope<KitBagItemDeleteCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<KitBagItemDeleteExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
