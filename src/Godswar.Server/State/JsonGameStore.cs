using System.Text.Json;

namespace Godswar.Server.State;

internal sealed class JsonGameStore : IGameStore
{
    private readonly string _statePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonGameStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _statePath = Path.Combine(dataPath, "state.json");
    }

    public async Task EnsureSeedDataAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_statePath))
            {
                return;
            }

            var db = new GameDatabase();
            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameAccount> LoginOrCreateAccountAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        username = CleanUsername(username);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var account = db.Accounts.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                account = new GameAccount
                {
                    Id = db.NextAccountId++,
                    Username = username,
                    Password = password,
                    CreatedUtc = DateTime.UtcNow
                };
                db.Accounts.Add(account);
            }
            else
            {
                // Local emulator mode: keep login friction low while packets are still being mapped.
                account.Password = password;
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(account);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task MarkAccountOfflineAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return;
            }

            character.CurrentMap = currentMap;
            character.PositionX = positionX;
            character.PositionZ = positionZ;
            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return;
            }

            character.CurrentHp = Math.Clamp(currentHp, 0, Math.Max(1, character.MaxHp));
            character.CurrentMp = Math.Clamp(currentMp, 0, Math.Max(0, character.MaxMp));
            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(int accountId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            return db.Characters
                .Where(c => c.AccountId == accountId)
                .OrderBy(c => c.Id)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> GetFirstCharacterAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return (await GetCharactersAsync(accountId, cancellationToken)).FirstOrDefault();
    }

    public async Task<CharacterStats?> GetCharacterStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            return character is null ? null : CharacterStats.FromCharacter(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter> CreateCharacterAsync(int accountId, GameCharacter character, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            character.Name = EnsureUniqueCharacterName(db, CleanCharacterName(character.Name));
            character.Id = db.NextCharacterId++;
            character.AccountId = accountId;
            GameDefaults.InitializeStartingLocation(character);
            character.Equipment = string.IsNullOrWhiteSpace(character.Equipment)
                ? GameDefaults.DefaultEquipment(character.Profession)
                : character.Equipment;
            character.KitBag = string.IsNullOrWhiteSpace(character.KitBag)
                ? GameDefaults.DefaultKitBag
                : character.KitBag;
            character.CreatedUtc = DateTime.UtcNow;
            db.Characters.Add(character);

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteCharacterAsync(int accountId, string characterName, CancellationToken cancellationToken = default)
    {
        characterName = CleanCharacterName(characterName);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var removed = db.Characters.RemoveAll(c =>
                c.AccountId == accountId &&
                string.Equals(c.Name, characterName, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                await SaveUnsafeAsync(db, cancellationToken);
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> MoveEquipmentToKitBagAsync(
        int accountId,
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
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

            var equipmentEntry = EquipmentSlots.GetEntry(character.Equipment, character.Profession, equipmentSlot);
            character.Equipment = EquipmentSlots.ClearSlot(character.Equipment, character.Profession, equipmentSlot);
            if (equipmentEntry != "[]")
            {
                character.KitBag = KitBagSlots.SetSlot(character.KitBag, kitBagSlot, equipmentEntry);
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> MoveKitBagToEquipmentAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        int requestedEquipmentSlot,
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

            var kitBagEntry = KitBagSlots.GetEntry(character.KitBag, kitBagSlot);
            var item = CompactItemEntry.Parse(kitBagEntry);
            if (!item.IsEmpty && kitBagEntry != "[]")
            {
                var equipmentSlot = EquipmentSlots.ResolveSlotForItem(
                    item.Id,
                    requestedEquipmentSlot,
                    character.Equipment,
                    character.Profession,
                    EquipmentSlots.ResolveSlotForItem(item.Id, requestedEquipmentSlot));
                var previousEquipmentEntry = EquipmentSlots.GetEntry(character.Equipment, character.Profession, equipmentSlot);
                character.Equipment = EquipmentSlots.SetSlot(character.Equipment, character.Profession, equipmentSlot, kitBagEntry);
                character.KitBag = previousEquipmentEntry == "[]"
                    ? KitBagSlots.ClearSlot(character.KitBag, kitBagSlot)
                    : KitBagSlots.SetSlot(character.KitBag, kitBagSlot, previousEquipmentEntry);
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> MoveKitBagItemAsync(
        int accountId,
        int characterId,
        int sourceSlot,
        int destinationSlot,
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

            if (sourceSlot != destinationSlot)
            {
                var sourceEntry = KitBagSlots.GetEntry(character.KitBag, sourceSlot);
                if (sourceEntry != "[]")
                {
                    var destinationEntry = KitBagSlots.GetEntry(character.KitBag, destinationSlot);
                    var updatedKitBag = KitBagSlots.SetSlot(character.KitBag, destinationSlot, sourceEntry);
                    character.KitBag = destinationEntry == "[]"
                        ? KitBagSlots.ClearSlot(updatedKitBag, sourceSlot)
                        : KitBagSlots.SetSlot(updatedKitBag, sourceSlot, destinationEntry);
                }
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> ApplyWeaponHolyStoneAsync(
        int accountId,
        int characterId,
        HolyStoneOperation operation,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
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

            if (!HolyStoneItemMutator.TryApply(
                    character.Equipment,
                    character.KitBag,
                    character.Profession,
                    operation,
                    targetKitBagSlot,
                    socketIndex,
                    stoneKitBagSlot,
                    destinationKitBagSlot,
                    out var equipment,
                    out var kitBag,
                    out _))
            {
                return null;
            }

            character.Equipment = equipment;
            character.KitBag = kitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

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

            var currentRank = Math.Max(0, clientRank);
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

    public Task<IReadOnlyList<TalentState>> GetTalentStatesAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<TalentState>>([]);
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
                    skill.PreviousSkillId is null &&
                    skill.SkillLevel == 1 &&
                    (skill.MinLevel ?? 1) <= character.Level &&
                    skill.ClassIds.Contains((short)character.Profession))
                .OrderBy(skill => skill.SkillId)
                .Select(skill => new SkillState
                {
                    SkillId = skill.SkillId,
                    Level = skill.SkillLevel!.Value
                })
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<IReadOnlyList<CapturedNpcSpawn>> GetCapturedNpcSpawnsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<CapturedNpcSpawn>>([]);
    }

    public Task<IReadOnlyList<NpcSpawnDefinition>> GetNpcSpawnDefinitionsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var references = NpcSpawnDefinitionFactory.FromGeneratedSeeds(mapId);
        var definitions = NpcSpawnDefinitionFactory.Create(mapId, [], [], references);
        return Task.FromResult(definitions);
    }

    public Task<IReadOnlyList<CapturedMonsterSpawn>> GetCapturedMonsterSpawnsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<CapturedMonsterSpawn>>([]);
    }

    public Task<IReadOnlyList<byte[]>> GetEnterSyncPacketsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<byte[]>>([]);
    }

    public ValueTask DisposeAsync()
    {
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<GameDatabase> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return new GameDatabase();
        }

        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<GameDatabase>(stream, JsonDefaults.Indented, cancellationToken)
            ?? new GameDatabase();
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
            CurrentMap = character.CurrentMap,
            Level = character.Level,
            MaxHp = character.MaxHp,
            MaxMp = character.MaxMp,
            CurrentHp = character.CurrentHp,
            CurrentMp = character.CurrentMp,
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
                ? GameDefaults.DefaultKitBag
                : character.KitBag,
            CreatedUtc = character.CreatedUtc,
            CalculatedStats = character.CalculatedStats
        };
    }
}
