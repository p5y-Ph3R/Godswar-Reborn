using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal sealed class PetBasicSavvyPreviewTestExecutor :
    DelegatingPetDurableCommandExecutor,
    IPetBasicSavvyPreviewLifecycleStore
{
    public bool PreviewIsCurrent { get; set; } = true;

    public int CurrentChecks { get; private set; }

    public int Discards { get; private set; }

    public Task<bool> IsCurrentAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid connectionId,
        Guid previewOperationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentChecks++;
        return Task.FromResult(PreviewIsCurrent);
    }

    public Task DiscardForSessionAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Discards++;
        return Task.CompletedTask;
    }
}
