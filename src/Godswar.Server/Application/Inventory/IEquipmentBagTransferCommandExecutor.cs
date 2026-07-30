using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IEquipmentBagTransferCommandExecutor
{
    Task<EquipmentBagTransferExecutionResult> ExecuteAsync(
        CommandEnvelope<EquipmentBagTransferCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<EquipmentBagTransferExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid clientOperationId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken = default);
}
