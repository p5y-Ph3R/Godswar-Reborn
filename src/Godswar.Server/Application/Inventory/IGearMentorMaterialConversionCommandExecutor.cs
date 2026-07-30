using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IGearMentorMaterialConversionCommandExecutor
{
    Task<GearMentorMaterialConversionExecutionResult> ExecuteAsync(
        CommandEnvelope<GearMentorTransformCrystalCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<GearMentorMaterialConversionExecutionResult> ExecuteAsync(
        CommandEnvelope<GearMentorCombineGemPiecesCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<GearMentorMaterialConversionExecutionResult>
        TryReplayTransformAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            Guid clientOperationId,
            CancellationToken cancellationToken = default);

    Task<GearMentorMaterialConversionExecutionResult>
        TryReplayCombineAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            Guid clientOperationId,
            CancellationToken cancellationToken = default);
}
