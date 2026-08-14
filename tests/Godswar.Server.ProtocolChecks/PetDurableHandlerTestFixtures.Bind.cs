using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal partial class DelegatingPetDurableCommandExecutor
{
    public Func<CommandEnvelope<PetBindCommand>,
        PetDurableExecutionResult>? BindPet { get; init; }

    public int BindPetCount { get; private set; }

    public CommandEnvelope<PetBindCommand>?
        BindPetEnvelope { get; private set; }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetBindCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BindPetCount++;
        BindPetEnvelope = envelope;
        return Task.FromResult(
            (BindPet ?? throw Missing("pet bind"))(envelope));
    }
}
