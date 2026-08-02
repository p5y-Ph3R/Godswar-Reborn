using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal interface IHolySuitCommandExecutor
{
    Task<HolySuitStoreQuotaSnapshot> ReadStoreQuotaAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default);

    Task<HolySuitExecutionResult> ExecuteAsync(
        CommandEnvelope<HolySuitCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<HolySuitExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        HolySuitCommandOperation operation,
        HolySuitOperationIdentity identity,
        CancellationToken cancellationToken = default);
}
