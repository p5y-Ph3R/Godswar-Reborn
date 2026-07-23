namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public async Task<TalentUpgradeResult?> UpgradeTalentAsync(
        int accountId,
        int characterId,
        int talentId,
        int clientRank,
        int clientTalentPoints,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            if (!SkillTalentSeeds.Talents.Any(talent =>
                    talent.Id == talentId &&
                    talent.ClassId == character.Profession))
            {
                return null;
            }

            var savedTalent = db.CharacterTalents.FirstOrDefault(talent =>
                talent.CharacterId == character.Id &&
                talent.TalentId == talentId);

            // The client values are UI echoes. Rank, cost, and spendable points
            // are all derived from the state held under this store's lock.
            var currentRank = Math.Clamp(savedTalent?.Rank ?? 0, 0, TalentProgression.RankCap);
            if (currentRank >= TalentProgression.RankCap)
            {
                return null;
            }

            var requiredPlayerLevel = TalentProgression.CalculateRequiredPlayerLevel(currentRank);
            if (character.Level < requiredPlayerLevel)
            {
                return null;
            }

            var cost = TalentProgression.CalculateUpgradeCost(currentRank);
            if (character.TalentPoints < cost)
            {
                return null;
            }

            var newRank = currentRank + 1;
            character.TalentPoints -= cost;
            if (savedTalent is null)
            {
                db.CharacterTalents.Add(new GameCharacterTalent
                {
                    CharacterId = character.Id,
                    TalentId = talentId,
                    Rank = newRank
                });
            }
            else
            {
                savedTalent.Rank = newRank;
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return new TalentUpgradeResult
            {
                Character = Clone(character),
                TalentId = talentId,
                NewRank = newRank,
                Cost = cost,
                RemainingTalentPoints = character.TalentPoints,
                DisplayValue = TalentProgression.CalculateDisplayValue(newRank)
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TalentState>> GetTalentStatesAsync(
        int accountId,
        int characterId,
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
                return [];
            }

            var savedRanks = db.CharacterTalents
                .Where(talent => talent.CharacterId == character.Id)
                .GroupBy(talent => talent.TalentId)
                .ToDictionary(
                    group => group.Key,
                    group => Math.Clamp(
                        group.Max(talent => talent.Rank),
                        0,
                        TalentProgression.RankCap));

            return SkillTalentSeeds.Talents
                .Where(talent => talent.ClassId == character.Profession)
                .OrderBy(talent => talent.TreeOrder)
                .ThenBy(talent => talent.Id)
                .Select(talent =>
                {
                    var rank = savedRanks.GetValueOrDefault(talent.Id);
                    return new TalentState
                    {
                        TalentId = talent.Id,
                        Rank = rank,
                        DisplayValue = TalentProgression.CalculateDisplayValue(rank),
                        NextCost = TalentProgression.CalculateUpgradeCost(rank)
                    };
                })
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SkillState>> GetSkillStatesAsync(
        int accountId,
        int characterId,
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
                return [];
            }

            return SkillTalentSeeds.Skills
                .Where(skill =>
                    (skill.SkillId == MountCatalog.RideSkillId ||
                     (skill.PreviousSkillId is null &&
                      skill.SkillLevel == 1 &&
                      (skill.MinLevel ?? 1) <= character.Level)) &&
                    skill.ClassIds.Contains((short)character.Profession))
                .OrderBy(skill => skill.SkillId)
                .Select(skill => new SkillState
                {
                    SkillId = skill.SkillId,
                    Level = skill.SkillId == MountCatalog.RideSkillId
                        ? (short)1
                        : skill.SkillLevel!.Value
                })
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

}
