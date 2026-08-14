using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal partial class DelegatingPetDurableCommandExecutor
{
    public Func<CommandEnvelope<PetAppearanceChangeCommand>,
        PetDurableExecutionResult>? ChangeAppearance { get; init; }

    public int ChangeAppearanceCount { get; private set; }

    public CommandEnvelope<PetAppearanceChangeCommand>?
        ChangeAppearanceEnvelope { get; private set; }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetAppearanceChangeCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChangeAppearanceCount++;
        ChangeAppearanceEnvelope = envelope;
        return Task.FromResult(
            (ChangeAppearance ?? throw Missing("pet appearance change"))(
                envelope));
    }
}
