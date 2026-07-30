using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IGearMentorDecomposeGearCommandExecutor
{
    Task<GearMentorDecomposeGearExecutionResult> ExecuteAsync(
        CommandEnvelope<GearMentorDecomposeGearCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<GearMentorDecomposeGearExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}
