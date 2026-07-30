using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Progression;

internal interface IProgressionIntervalSettlementCommandExecutor
{
    Task<ProgressionIntervalSettlementExecutionResult> ExecuteAsync(
        CommandEnvelope<ProgressionIntervalSettlementCommand> envelope,
        CancellationToken cancellationToken = default);
}
