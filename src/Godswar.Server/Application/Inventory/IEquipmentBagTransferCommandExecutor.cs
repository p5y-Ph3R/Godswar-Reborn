using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IEquipmentBagTransferCommandExecutor
{
    Task<EquipmentBagTransferExecutionResult> ExecuteAsync(
        CommandEnvelope<EquipmentBagTransferCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<EquipmentBagTransferExecutionResult> TryReplayAsync(
        CommandSubject subject,
        Guid clientOperationId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken = default);
}
