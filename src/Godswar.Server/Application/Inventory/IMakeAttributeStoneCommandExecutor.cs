using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IMakeAttributeStoneCommandExecutor
{
    Task<MakeAttributeStoneExecutionResult> ExecuteAsync(
        CommandEnvelope<GearMentorMakeAttributeStoneCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<MakeAttributeStoneExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
