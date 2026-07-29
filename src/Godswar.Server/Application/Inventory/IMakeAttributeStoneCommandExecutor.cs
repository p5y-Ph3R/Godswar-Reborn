using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IMakeAttributeStoneCommandExecutor
{
    Task<MakeAttributeStoneExecutionResult> ExecuteAsync(
        CommandEnvelope<GearMentorMakeAttributeStoneCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<MakeAttributeStoneExecutionResult> TryReplayAsync(
        CommandSubject subject,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
