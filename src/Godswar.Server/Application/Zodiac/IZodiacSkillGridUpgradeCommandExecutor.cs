using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal interface IZodiacSkillGridUpgradeCommandExecutor
{
    Task<ZodiacSkillGridUpgradeExecutionResult> ExecuteAsync(
        CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
        CancellationToken cancellationToken = default);
}
