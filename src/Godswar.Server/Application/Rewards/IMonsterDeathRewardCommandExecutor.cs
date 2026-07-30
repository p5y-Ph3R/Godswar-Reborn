using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Rewards;

internal interface IMonsterDeathRewardCommandExecutor
{
    Task<MonsterDeathRewardExecutionResult> ExecuteAsync(
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        CancellationToken cancellationToken = default);
}
