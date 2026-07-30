using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IEquipmentForgeCommandExecutor
{
    Task<EquipmentForgeExecutionResult> ExecuteAsync(
        CommandEnvelope<EquipmentForgeCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<EquipmentForgeExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
