using System.Text.Json;

namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public ValueTask DisposeAsync()
    {
        // The semaphore is shared by every store instance addressing this
        // path. It intentionally lives for the process lifetime so disposing
        // one instance cannot invalidate another active store.
        return ValueTask.CompletedTask;
    }

    private async Task<GameDatabase> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return new GameDatabase();
        }

        await using var stream = File.OpenRead(_statePath);
        var db = await JsonSerializer.DeserializeAsync<GameDatabase>(stream, JsonDefaults.Indented, cancellationToken)
            ?? new GameDatabase();
        db.Accounts ??= [];
        db.Characters ??= [];
        db.CharacterTalents ??= [];
        db.CharacterExperienceBoosts ??= [];
        db.FactionAreaExperienceControls ??= [];
        return db;
    }

    private async Task SaveUnsafeAsync(GameDatabase db, CancellationToken cancellationToken)
    {
        var tempPath = _statePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, db, JsonDefaults.Indented, cancellationToken);
        }

        File.Move(tempPath, _statePath, overwrite: true);
    }

    private static string CleanUsername(string username)
    {
        username = username.Trim('\0', ' ', '\t', '\r', '\n');
        return string.IsNullOrWhiteSpace(username) ? "player" : username;
    }

    private static string CleanCharacterName(string name)
    {
        name = name.Trim('\0', ' ', '\t', '\r', '\n');
        return string.IsNullOrWhiteSpace(name) ? $"Hero{Random.Shared.Next(1000, 9999)}" : name;
    }

    private static string EnsureUniqueCharacterName(GameDatabase db, string requestedName)
    {
        if (!db.Characters.Any(c => string.Equals(c.Name, requestedName, StringComparison.OrdinalIgnoreCase)))
        {
            return requestedName;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{requestedName}{i}";
            if (!db.Characters.Any(c => string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{requestedName}{Guid.NewGuid():N}"[..32];
    }

    private static GameAccount Clone(GameAccount account)
    {
        return new GameAccount
        {
            Id = account.Id,
            Username = account.Username,
            Password = account.Password,
            VipTier = account.VipTier,
            VipExpiresAt = account.VipExpiresAt,
            CreatedUtc = account.CreatedUtc
        };
    }

    private static GameCharacter Clone(GameCharacter character)
    {
        return new GameCharacter
        {
            Id = character.Id,
            AccountId = character.AccountId,
            Name = character.Name,
            Gender = character.Gender,
            Camp = character.Camp,
            Profession = character.Profession,
            Hair = character.Hair,
            Face = character.Face,
            Faith = character.Faith,
            ZodiacType = character.ZodiacType,
            ZodiacLuckyStatus = character.ZodiacLuckyStatus,
            ZodiacLuckyExpiresAt = character.ZodiacLuckyExpiresAt,
            ZodiacLevel = character.ZodiacLevel,
            ZodiacEnergy = character.ZodiacEnergy,
            ZodiacEnergyRemainderX100 = character.ZodiacEnergyRemainderX100,
            ZodiacOnlineDay = character.ZodiacOnlineDay,
            ZodiacOnlineDurationTicksToday = character.ZodiacOnlineDurationTicksToday,
            ZodiacLastOnlineAt = character.ZodiacLastOnlineAt,
            ZodiacLastCompensationDay = character.ZodiacLastCompensationDay,
            ZodiacAccumulatedExperienceX100 = character.ZodiacAccumulatedExperienceX100,
            ZodiacAccumulatedTalentExperienceX100 = character.ZodiacAccumulatedTalentExperienceX100,
            ZodiacSkillGridLevels =
                ZodiacSkillGridActivation.NormalizeLevels(
                    character.ZodiacSkillGridLevels),
            ZodiacSkillGridSkillIds =
                ZodiacSkillGridActivation.NormalizeSkillIds(
                    character.ZodiacSkillGridSkillIds),
            CurrentMap = character.CurrentMap,
            Level = character.Level,
            Experience = character.Experience,
            Silver = character.Silver,
            Gold = character.Gold,
            MaxHp = character.MaxHp,
            MaxMp = character.MaxMp,
            CurrentHp = character.CurrentHp,
            CurrentMp = character.CurrentMp,
            VitalsRevision = character.VitalsRevision,
            TalentPoints = character.TalentPoints,
            TalentExperience = character.TalentExperience,
            HolySuitPoints = character.HolySuitPoints,
            WeaponRank = character.WeaponRank,
            WeaponAuraEffect = character.WeaponAuraEffect,
            ArmorRank = character.ArmorRank,
            ArmorAuraEffect = character.ArmorAuraEffect,
            PositionX = character.PositionX,
            PositionZ = character.PositionZ,
            Equipment = string.IsNullOrWhiteSpace(character.Equipment)
                ? GameDefaults.DefaultEquipment(character.Profession)
                : character.Equipment,
            KitBag = string.IsNullOrWhiteSpace(character.KitBag)
                ? GameDefaults.EmptyKitBag
                : character.KitBag,
            CreatedUtc = character.CreatedUtc,
            CalculatedStats = character.CalculatedStats
        };
    }
}
