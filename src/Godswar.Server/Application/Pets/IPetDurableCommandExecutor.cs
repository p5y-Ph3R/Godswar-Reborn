using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal interface IPetDurableCommandExecutor
{
    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<BagItemActivationCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetLevelUpgradeCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetPresenceTransitionCommand> envelope,
        CancellationToken cancellationToken = default);
}
