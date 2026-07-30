using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<ZodiacSkillGridSelectionExecutionResult>
        ExecuteDurableZodiacSkillGridSelectionAsync(
            ClientSession session,
            int accountId,
            GameCharacter character,
            IZodiacSkillGridSelectionCommandExecutor executor,
            CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Subject.AccountId != accountId ||
            envelope.Subject.CharacterId != character.Id)
        {
            return ZodiacSkillGridSelectionExecutionResult
                .PreconditionFailed();
        }

        if (!_zodiacOnlineSessions.TryGetValue(session, out var state))
        {
            var result = await executor.ExecuteAsync(
                envelope,
                cancellationToken);
            ValidateSelectionProjection(
                character.Id,
                envelope.Command.GridIndex,
                result);
            ApplySelectionProjection(
                character,
                envelope.Command.GridIndex,
                result);
            return result;
        }

        if (state.AccountId != accountId ||
            state.CharacterId != character.Id)
        {
            return ZodiacSkillGridSelectionExecutionResult
                .PreconditionFailed();
        }

        // Selection shares the same owner gate as energy accrual, activation,
        // and grid upgrades so neither live character mirror can go stale.
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var result = await executor.ExecuteAsync(
                envelope,
                cancellationToken);
            ValidateSelectionProjection(
                character.Id,
                envelope.Command.GridIndex,
                result);
            ApplySelectionProjection(
                state.Character,
                envelope.Command.GridIndex,
                result);
            if (!ReferenceEquals(state.Character, character))
            {
                ApplySelectionProjection(
                    character,
                    envelope.Command.GridIndex,
                    result);
            }

            if (result.HasAuthoritativeProjection)
            {
                UpdateCharacter(
                    session,
                    character,
                    advanceWorldRevision: false);
            }

            return result;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static void ValidateSelectionProjection(
        int characterId,
        int gridIndex,
        ZodiacSkillGridSelectionExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.HasAuthoritativeProjection)
        {
            return;
        }

        var receipt = result.Receipt ??
            throw new InvalidDataException(
                "A Zodiac selection projection has no receipt.");
        if (receipt.Family !=
                CommandFamily.ZodiacSkillGridSelection ||
            receipt.CharacterId != characterId ||
            receipt.GridIndex != gridIndex)
        {
            throw new InvalidDataException(
                "The Zodiac selection receipt identity is inconsistent.");
        }
    }

    private static void ApplySelectionProjection(
        GameCharacter character,
        int gridIndex,
        ZodiacSkillGridSelectionExecutionResult result)
    {
        if (!result.HasAuthoritativeProjection)
        {
            return;
        }

        lock (character.ZodiacSync)
        {
            character.ZodiacSkillGridLevels =
                ZodiacSkillGridActivation.NormalizeLevels(
                    character.ZodiacSkillGridLevels);
            character.ZodiacSkillGridSkillIds =
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    character.ZodiacSkillGridSkillIds);
            character.ZodiacSkillGridLevels[gridIndex] =
                result.CurrentLevel;
            character.ZodiacSkillGridSkillIds[gridIndex] =
                result.SelectedSkillKind;
        }
    }
}
