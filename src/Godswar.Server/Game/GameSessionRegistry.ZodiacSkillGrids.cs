using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<ZodiacSkillGridActivationResult?>
        ActivateZodiacSkillGridAsync(
            ClientSession session,
            int accountId,
            GameCharacter character,
            int gridIndex,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        if (_store is null)
        {
            return null;
        }

        if (!_zodiacOnlineSessions.TryGetValue(session, out var state))
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.ActivateZodiacSkillGrid);
            var untrackedResult =
                await _store.ActivateZodiacSkillGridAsync(
                    accountId,
                    character.Id,
                    gridIndex,
                    cancellationToken);
            if (untrackedResult is not null)
            {
                ApplyZodiacSkillGridActivationResult(
                    character,
                    untrackedResult);
            }

            return untrackedResult;
        }

        if (state.AccountId != accountId ||
            state.CharacterId != character.Id)
        {
            return null;
        }

        // Share the Zodiac persistence gate with energy accrual and level-up.
        // This prevents a stale live mirror from replacing the committed grid
        // or premium-gold balance while the activation is in flight.
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.ActivateZodiacSkillGrid);
            var result = await _store.ActivateZodiacSkillGridAsync(
                accountId,
                character.Id,
                gridIndex,
                cancellationToken);
            if (result is null)
            {
                return null;
            }

            ApplyZodiacSkillGridActivationResult(state.Character, result);
            if (!ReferenceEquals(state.Character, character))
            {
                ApplyZodiacSkillGridActivationResult(character, result);
            }

            return result;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<ZodiacSkillGridUpgradeResult?>
        UpgradeZodiacSkillGridAsync(
            ClientSession session,
            int accountId,
            GameCharacter character,
            int gridIndex,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        if (_store is null)
        {
            return null;
        }

        if (!_zodiacOnlineSessions.TryGetValue(session, out var state))
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.UpgradeZodiacSkillGrid);
            var untrackedResult =
                await _store.UpgradeZodiacSkillGridAsync(
                    accountId,
                    character.Id,
                    gridIndex,
                    cancellationToken);
            if (untrackedResult is not null)
            {
                ApplyZodiacSkillGridUpgradeResult(
                    character,
                    untrackedResult);
            }

            return untrackedResult;
        }

        if (state.AccountId != accountId ||
            state.CharacterId != character.Id)
        {
            return null;
        }

        // Energy accrual, Zodiac-level changes, activation, and grid upgrades
        // share one live-session gate. The store separately locks the durable
        // character row, including against normal Talent Point spending.
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.UpgradeZodiacSkillGrid);
            var result = await _store.UpgradeZodiacSkillGridAsync(
                accountId,
                character.Id,
                gridIndex,
                cancellationToken);
            if (result is null)
            {
                return null;
            }

            ApplyZodiacSkillGridUpgradeResult(state.Character, result);
            if (!ReferenceEquals(state.Character, character))
            {
                ApplyZodiacSkillGridUpgradeResult(character, result);
            }

            return result;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<ZodiacSkillGridSelectionResult?>
        SelectZodiacSkillGridAsync(
            ClientSession session,
            int accountId,
            GameCharacter character,
            int gridIndex,
            int selectedSkillKind,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        if (_store is null)
        {
            return null;
        }

        if (!_zodiacOnlineSessions.TryGetValue(session, out var state))
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.SelectZodiacSkillGrid);
            var result = await _store.SelectZodiacSkillGridAsync(
                accountId,
                character.Id,
                gridIndex,
                selectedSkillKind,
                cancellationToken);
            if (result is not null)
            {
                ApplyZodiacSkillGridSelectionResult(character, result);
            }

            return result;
        }

        if (state.AccountId != accountId ||
            state.CharacterId != character.Id)
        {
            return null;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.SelectZodiacSkillGrid);
            var result = await _store.SelectZodiacSkillGridAsync(
                accountId,
                character.Id,
                gridIndex,
                selectedSkillKind,
                cancellationToken);
            if (result is null)
            {
                return null;
            }

            ApplyZodiacSkillGridSelectionResult(state.Character, result);
            if (!ReferenceEquals(state.Character, character))
            {
                ApplyZodiacSkillGridSelectionResult(character, result);
            }

            return result;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static void ApplyZodiacSkillGridActivationResult(
        GameCharacter character,
        ZodiacSkillGridActivationResult result)
    {
        lock (character.ZodiacSync)
        {
            character.Gold = result.CurrentGold;
            if (!ZodiacSkillGridCatalog.IsValidGrid(result.GridIndex))
            {
                return;
            }

            character.ZodiacSkillGridLevels =
                ZodiacSkillGridActivation.NormalizeLevels(
                    character.ZodiacSkillGridLevels);
            character.ZodiacSkillGridSkillIds =
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    character.ZodiacSkillGridSkillIds);
            character.ZodiacSkillGridLevels[result.GridIndex] =
                result.CurrentLevel;
            character.ZodiacSkillGridSkillIds[result.GridIndex] =
                result.SelectedSkillId;
        }
    }

    private static void ApplyZodiacSkillGridUpgradeResult(
        GameCharacter character,
        ZodiacSkillGridUpgradeResult result)
    {
        lock (character.ZodiacSync)
        {
            character.ZodiacEnergy = result.CurrentEnergy;
            character.ZodiacEnergyRemainderX100 =
                result.CurrentEnergyRemainderX100;
            character.TalentPoints = result.CurrentTalentPoints;
            if (!ZodiacSkillGridCatalog.IsValidGrid(result.GridIndex))
            {
                return;
            }

            character.ZodiacSkillGridLevels =
                ZodiacSkillGridActivation.NormalizeLevels(
                    character.ZodiacSkillGridLevels);
            character.ZodiacSkillGridSkillIds =
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    character.ZodiacSkillGridSkillIds);
            character.ZodiacSkillGridLevels[result.GridIndex] =
                result.CurrentLevel;
            character.ZodiacSkillGridSkillIds[result.GridIndex] =
                result.SelectedSkillId;
        }
    }

    private static void ApplyZodiacSkillGridSelectionResult(
        GameCharacter character,
        ZodiacSkillGridSelectionResult result)
    {
        if (!ZodiacSkillGridCatalog.IsValidGrid(result.GridIndex))
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
            character.ZodiacSkillGridLevels[result.GridIndex] =
                result.CurrentLevel;
            character.ZodiacSkillGridSkillIds[result.GridIndex] =
                result.SelectedSkillKind;
        }
    }
}
