namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public async Task<ZodiacSkillGridActivationResult?>
        ActivateZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId);
            if (character is null)
            {
                return null;
            }

            var result = ZodiacSkillGridActivation.Apply(
                character,
                gridIndex);
            if (result.Committed)
            {
                await SaveUnsafeAsync(db, cancellationToken);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ZodiacSkillGridUpgradeResult?>
        UpgradeZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId);
            if (character is null)
            {
                return null;
            }

            var result = ZodiacSkillGridUpgrade.Apply(
                character,
                gridIndex);
            if (result.Committed)
            {
                await SaveUnsafeAsync(db, cancellationToken);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ZodiacSkillGridSelectionResult?>
        SelectZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            int selectedSkillKind,
            CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId);
            if (character is null)
            {
                return null;
            }

            var learned =
                selectedSkillKind ==
                    ZodiacSkillGridSelectionCatalog.ClearSelection ||
                SkillTalentSeeds.Skills.Any(skill =>
                    ZodiacSkillGridSelectionCatalog
                        .IsRuntimeSkillInFamily(
                            selectedSkillKind,
                            skill.SkillId) &&
                    skill.ClassIds.Contains(
                        checked((short)character.Profession)) &&
                    skill.PreviousSkillId is null &&
                    skill.SkillLevel == 1 &&
                    (skill.MinLevel ?? 1) <= character.Level);
            var result = ZodiacSkillGridSelection.Apply(
                character,
                gridIndex,
                selectedSkillKind,
                learned);
            if (result.Committed)
            {
                await SaveUnsafeAsync(db, cancellationToken);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }
}
