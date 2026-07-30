using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IKitBagItemMoveCommandExecutor
{
    Task<KitBagItemMoveExecutionResult> ExecuteAsync(
        CommandEnvelope<KitBagItemMoveCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<KitBagItemMoveExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid clientOperationId,
        int sourceKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default);
}
