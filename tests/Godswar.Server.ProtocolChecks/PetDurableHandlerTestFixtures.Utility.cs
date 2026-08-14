using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal partial class DelegatingPetDurableCommandExecutor
{
    public Func<CommandEnvelope<PetManagerUtilityCommand>,
        PetDurableExecutionResult>? PetManagerUtility { get; init; }

    public int PetManagerUtilityCount { get; private set; }

    public CommandEnvelope<PetManagerUtilityCommand>?
        PetManagerUtilityEnvelope { get; private set; }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PetManagerUtilityCount++;
        PetManagerUtilityEnvelope = envelope;
        return Task.FromResult(
            (PetManagerUtility ??
                throw Missing("Pet Manager utility"))(envelope));
    }
}
