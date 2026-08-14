using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal interface IPetGrowthPreviewLifecycleStore
{
    Task<bool> IsCurrentAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid connectionId,
        Guid previewOperationId,
        CancellationToken cancellationToken = default);

    Task DiscardForSessionAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid connectionId,
        CancellationToken cancellationToken = default);
}
