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

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetSkillUnlearnCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetGrowthResetCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetOwnerMergeToggleCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetToPetMergeCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetRebirthCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetAppearanceChangeCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetBindCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetSoulContractCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        CancellationToken cancellationToken = default);
}
