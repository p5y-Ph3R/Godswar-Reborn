using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<ZodiacSkillGridUpgradeExecutionResult>
        ExecuteDurableZodiacSkillGridUpgradeAsync(
            ClientSession session,
            int accountId,
            GameCharacter character,
            IZodiacSkillGridUpgradeCommandExecutor executor,
            CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Subject.AccountId != accountId ||
            envelope.Subject.CharacterId != character.Id ||
            envelope.Ownership.IsValid &&
            !IsCurrentAccountSession(
                accountId,
                session,
                envelope.Ownership))
        {
            return ZodiacSkillGridUpgradeExecutionResult
                .PreconditionFailed();
        }

        if (!_zodiacOnlineSessions.TryGetValue(session, out var state))
        {
            var untrackedExecution = await executor.ExecuteAsync(
                envelope,
                cancellationToken);
            if (envelope.Ownership.IsValid &&
                !IsCurrentAccountSession(
                    accountId,
                    session,
                    envelope.Ownership))
            {
                return ZodiacSkillGridUpgradeExecutionResult
                    .PreconditionFailed();
            }

            ValidateDurableZodiacSkillGridUpgradeProjection(
                character.Id,
                envelope.Command.GridIndex,
                untrackedExecution);
            ApplyDurableZodiacSkillGridUpgradeProjection(
                character,
                envelope.Command.GridIndex,
                untrackedExecution);
            return untrackedExecution;
        }

        if (state.AccountId != accountId ||
            state.CharacterId != character.Id)
        {
            return ZodiacSkillGridUpgradeExecutionResult
                .PreconditionFailed();
        }

        // Online Zodiac accrual, level changes, activation, and repeatable
        // grid upgrades share this gate. The durable transaction owns the
        // database row; this gate owns the ordering of both live mirrors.
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var execution = await executor.ExecuteAsync(
                envelope,
                cancellationToken);
            if (envelope.Ownership.IsValid &&
                !IsCurrentAccountSession(
                    accountId,
                    session,
                    envelope.Ownership))
            {
                return ZodiacSkillGridUpgradeExecutionResult
                    .PreconditionFailed();
            }

            ValidateDurableZodiacSkillGridUpgradeProjection(
                character.Id,
                envelope.Command.GridIndex,
                execution);
            ApplyDurableZodiacSkillGridUpgradeProjection(
                state.Character,
                envelope.Command.GridIndex,
                execution);
            if (!ReferenceEquals(state.Character, character))
            {
                ApplyDurableZodiacSkillGridUpgradeProjection(
                    character,
                    envelope.Command.GridIndex,
                    execution);
            }
            if (execution.HasAuthoritativeProjection)
            {
                // Publish the handler mirror while the Zodiac gate is still
                // held. A concurrent online-energy tick must never update an
                // old registry mirror that is replaced after gate release.
                UpdateCharacter(
                    session,
                    character,
                    advanceWorldRevision: false);
            }

            return execution;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static void ValidateDurableZodiacSkillGridUpgradeProjection(
        int characterId,
        int gridIndex,
        ZodiacSkillGridUpgradeExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!execution.HasAuthoritativeProjection)
        {
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Zodiac grid projection has no receipt.");
        if (receipt.Family != CommandFamily.ZodiacSkillGridUpgrade ||
            receipt.CharacterId != characterId ||
            receipt.GridIndex != gridIndex)
        {
            throw new InvalidDataException(
                "The durable Zodiac grid receipt identity is inconsistent.");
        }
    }

    private static void ApplyDurableZodiacSkillGridUpgradeProjection(
        GameCharacter character,
        int gridIndex,
        ZodiacSkillGridUpgradeExecutionResult execution)
    {
        if (!execution.HasAuthoritativeProjection)
        {
            return;
        }

        lock (character.ZodiacSync)
        {
            character.ZodiacEnergy = execution.CurrentEnergy;
            character.ZodiacEnergyRemainderX100 =
                execution.CurrentEnergyRemainderX100;
            character.TalentPoints = execution.CurrentTalentPoints;
            character.ZodiacSkillGridLevels =
                ZodiacSkillGridActivation.NormalizeLevels(
                    character.ZodiacSkillGridLevels);
            character.ZodiacSkillGridSkillIds =
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    character.ZodiacSkillGridSkillIds);
            character.ZodiacSkillGridLevels[gridIndex] =
                execution.CurrentLevel;
            character.ZodiacSkillGridSkillIds[gridIndex] =
                execution.SelectedSkillId;
        }
    }
}
