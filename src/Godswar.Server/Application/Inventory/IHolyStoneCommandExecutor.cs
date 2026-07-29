using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IHolyStoneCommandExecutor
{
    Task<HolyStoneExecutionResult> ExecuteAsync(
        CommandEnvelope<HolyStoneCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<HolyStoneExecutionResult> TryReplayAsync(
        CommandSubject subject,
        HolyStoneCommandOperation operation,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
