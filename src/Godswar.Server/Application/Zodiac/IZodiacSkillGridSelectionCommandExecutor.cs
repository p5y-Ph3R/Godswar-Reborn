using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal interface IZodiacSkillGridSelectionCommandExecutor
{
    Task<ZodiacSkillGridSelectionExecutionResult> ExecuteAsync(
        CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
        CancellationToken cancellationToken = default);
}
