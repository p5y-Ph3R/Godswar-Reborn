using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal interface IZodiacSkillGridActivationCommandExecutor
{
    Task<ZodiacSkillGridActivationExecutionResult> ExecuteAsync(
        CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
        CancellationToken cancellationToken = default);
}
