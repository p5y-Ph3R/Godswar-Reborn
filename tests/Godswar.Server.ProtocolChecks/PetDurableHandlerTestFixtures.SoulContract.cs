using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal partial class DelegatingPetDurableCommandExecutor
{
    public Func<CommandEnvelope<PetSoulContractCommand>,
        PetDurableExecutionResult>? SignSoulContract { get; init; }

    public int SignSoulContractCount { get; private set; }

    public CommandEnvelope<PetSoulContractCommand>?
        SignSoulContractEnvelope { get; private set; }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetSoulContractCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SignSoulContractCount++;
        SignSoulContractEnvelope = envelope;
        return Task.FromResult(
            (SignSoulContract ?? throw Missing("Soul Contract"))(envelope));
    }
}
