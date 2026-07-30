using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Characters;

internal interface ICharacterLifecycleCommandExecutor
{
    Task<CharacterLifecycleExecutionResult> ExecuteAsync(
        CommandEnvelope<CharacterCreateCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<CharacterLifecycleExecutionResult> ExecuteAsync(
        CommandEnvelope<CharacterDeleteCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<CharacterLifecycleExecutionResult> ExecuteAsync(
        CommandEnvelope<CharacterRestoreCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<CharacterLifecycleExecutionResult> ExecuteAsync(
        CommandEnvelope<CharacterPurgeCommand> envelope,
        CancellationToken cancellationToken = default);
}
