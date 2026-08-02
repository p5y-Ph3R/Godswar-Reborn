using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IClassSuitCommandExecutor
{
    Task<ClassSuitExecutionResult> ExecuteAsync(
        CommandEnvelope<ClassSuitCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<ClassSuitExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        ClassSuitReplayIntent replayIntent,
        ClassSuitOperationIdentity identity,
        CancellationToken cancellationToken = default);
}
