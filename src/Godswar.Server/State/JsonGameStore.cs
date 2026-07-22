using System.Collections.Concurrent;
using System.Text.Json;
using Godswar.Server.Game;

namespace Godswar.Server.State;

internal sealed class JsonGameStore : IGameStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _statePath;
    private readonly SemaphoreSlim _lock;

    public JsonGameStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _statePath = Path.GetFullPath(Path.Combine(dataPath, "state.json"));
        _lock = PathLocks.GetOrAdd(_statePath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task EnsureSeedDataAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = File.Exists(_statePath)
                ? await LoadUnsafeAsync(cancellationToken)
                : new GameDatabase();
            foreach (var boost in db.CharacterExperienceBoosts)
            {
                CharacterBoostOnlineDuration.RestoreLegacyGrant(boost);
            }

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
        long vitalsRevision,
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

            if (vitalsRevision <= character.VitalsRevision)
            {
                return;
            }

            character.CurrentHp = Math.Clamp(currentHp, 0, Math.Max(1, character.MaxHp));
            character.CurrentMp = Math.Clamp(currentMp, 0, Math.Max(0, character.MaxMp));
            character.VitalsRevision = vitalsRevision;
            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
        int accountId,
        int characterId,
        int experience,
        int talentExperience,
        CancellationToken cancellationToken = default)
    {
        experience = Math.Max(0, experience);
        talentExperience = Math.Max(0, talentExperience);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return null;
            }

            var previousLevel = character.Level;
            var fighterProgression = PlayerExperienceCatalog.Apply(
                character.Level,
                character.Experience,
                experience);
            character.Level = fighterProgression.Level;
            character.Experience = fighterProgression.Experience;
            var accumulatedTalentExperience = checked(character.TalentExperience + talentExperience);
            var gainedTalentPoints = accumulatedTalentExperience / 100;
            character.TalentExperience = accumulatedTalentExperience % 100;
            character.TalentPoints = checked(character.TalentPoints + gainedTalentPoints);
            await SaveUnsafeAsync(db, cancellationToken);

            return new CharacterProgressionResult(
                fighterProgression.ExperienceGained,
                previousLevel,
                character.Level,
                character.Experience,
                PlayerExperienceCatalog.GetNextLevelExperience(character.Level),
                fighterProgression.LevelUps,
                talentExperience,
                character.TalentExperience,
                gainedTalentPoints,
                character.TalentPoints);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ZodiacAccumulationResult?> AddZodiacAccumulationAsync(
        int accountId,
        int characterId,
        int experienceGainX100,
        int talentExperienceGainX100,
        CancellationToken cancellationToken = default)
    {
        experienceGainX100 = Math.Max(0, experienceGainX100);
        talentExperienceGainX100 = Math.Max(0, talentExperienceGainX100);

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

            character.ZodiacAccumulatedExperienceX100 = checked(
                character.ZodiacAccumulatedExperienceX100 + experienceGainX100);
            character.ZodiacAccumulatedTalentExperienceX100 = checked(
                character.ZodiacAccumulatedTalentExperienceX100 + talentExperienceGainX100);
            await SaveUnsafeAsync(db, cancellationToken);

            return new ZodiacAccumulationResult(
                experienceGainX100,
                talentExperienceGainX100,
                character.ZodiacAccumulatedExperienceX100,
                character.ZodiacAccumulatedTalentExperienceX100);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ZodiacEnergyAccrualResult?> ApplyZodiacOnlineTimeAsync(
        int accountId,
        int characterId,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil,
        ZodiacEnergyPolicy policy,
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

            var result = ZodiacEnergyAccrual.Apply(
                character,
                onlineFrom,
                onlineUntil,
                policy);
            await SaveUnsafeAsync(db, cancellationToken);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ExperienceBoostState> GetExperienceBoostStateAsync(
        int accountId,
        int characterId,
        byte camp,
        byte mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            if (!db.Characters.Any(character =>
                    character.Id == characterId &&
                    character.AccountId == accountId))
            {
                return ExperienceBoostState.Empty;
            }

            foreach (var boost in db.CharacterExperienceBoosts)
            {
                CharacterBoostOnlineDuration.RestoreLegacyGrant(boost);
            }

            var boosts = db.CharacterExperienceBoosts
                .Where(boost =>
                    boost.CharacterId == characterId &&
                    boost.ActivatedAt <= now &&
                    (!boost.RemainingOnlineTicks.HasValue ||
                     CharacterBoostOnlineDuration.RemainingTicks(boost) > 0))
                .GroupBy(boost => boost.Kind)
                .Select(group => group
                    .OrderByDescending(boost => boost.Priority)
                    .ThenByDescending(boost => boost.BonusBasisPoints)
                    .First())
                .Select(boost => new ActiveExperienceBoost(
                    boost.StatusId,
                    boost.Kind,
                    boost.BonusBasisPoints,
                    boost.Priority,
                    CharacterBoostOnlineDuration.EffectiveExpiry(boost, now),
                    boost.Source))
                .ToList();

            var account = db.Accounts.FirstOrDefault(candidate => candidate.Id == accountId);
            if (account is not null &&
                account.VipTier != VipTier.None &&
                (account.VipExpiresAt is null || account.VipExpiresAt > now))
            {
                boosts.Add(new ActiveExperienceBoost(
                    VipExperienceBoosts.StatusId(account.VipTier),
                    ExperienceBoostKinds.Vip,
                    VipExperienceBoosts.BonusBasisPoints(account.VipTier),
                    (int)account.VipTier,
                    account.VipExpiresAt,
                    $"vip:{account.VipTier.ToString().ToLowerInvariant()}"));
            }

            var areaControl = db.FactionAreaExperienceControls.FirstOrDefault(control =>
                control.MapId == mapId &&
                control.ControllingCamp == camp &&
                control.ActivatedAt <= now &&
                control.ExpiresAt > now &&
                WorldBossCatalog.Default.IsWorldBoss(mapId, control.BossTemplateKey));
            if (areaControl is not null)
            {
                boosts.Add(new ActiveExperienceBoost(
                    ExperienceStatusIds.FactionAreaExperience,
                    ExperienceBoostKinds.FactionArea,
                    areaControl.BonusBasisPoints,
                    1,
                    areaControl.ExpiresAt,
                    $"world-boss:{areaControl.BossTemplateKey}"));
            }

            return new ExperienceBoostState(boosts.OrderBy(boost => boost.Kind).ToArray());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ConsumeCharacterBoostOnlineTimeAsync(
        int accountId,
        int characterId,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil,
        CancellationToken cancellationToken = default)
    {
        if (onlineUntil <= onlineFrom)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            if (!db.Characters.Any(character =>
                    character.Id == characterId &&
                    character.AccountId == accountId))
            {
                return;
            }

            foreach (var boost in db.CharacterExperienceBoosts.Where(candidate =>
                         candidate.CharacterId == characterId))
            {
                CharacterBoostOnlineDuration.Consume(
                    boost,
                    onlineFrom,
                    onlineUntil);
            }

            await SaveUnsafeAsync(db, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FactionAreaExperienceControl?> ActivateWorldBossAreaAsync(
        short mapId,
        string bossTemplateKey,
        byte controllingCamp,
        DateTimeOffset killedAt,
        string deathToken,
        CancellationToken cancellationToken = default)
    {
        if (controllingCamp is not (GameDefaults.SpartaCamp or GameDefaults.AthensCamp) ||
            string.IsNullOrWhiteSpace(deathToken) ||
            !WorldBossCatalog.Default.IsWorldBoss(mapId, bossTemplateKey))
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var existing = db.FactionAreaExperienceControls.FirstOrDefault(control => control.MapId == mapId);
            if (existing is not null &&
                string.Equals(existing.DeathToken, deathToken, StringComparison.Ordinal))
            {
                return null;
            }

            var control = existing ?? new FactionAreaExperienceControl { MapId = checked((byte)mapId) };
            control.ControllingCamp = controllingCamp;
            control.BossTemplateKey = bossTemplateKey;
            control.DeathToken = deathToken;
            control.BonusBasisPoints = 2_500;
            control.ActivatedAt = killedAt;
            control.ExpiresAt = killedAt + WorldBossCatalog.Default.RespawnInterval;
            if (existing is null)
            {
                db.FactionAreaExperienceControls.Add(control);
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return control;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WorldBossRespawnState?> GetActiveWorldBossRespawnAsync(
        short mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var control = db.FactionAreaExperienceControls.FirstOrDefault(candidate =>
                candidate.MapId == mapId &&
                candidate.ExpiresAt > now &&
                WorldBossCatalog.Default.IsWorldBoss(mapId, candidate.BossTemplateKey));
            return control is null
                ? null
                : new WorldBossRespawnState(mapId, control.BossTemplateKey, control.ExpiresAt);
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
                ? GameDefaults.StarterKitBag
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
            var removedCharacterIds = db.Characters
                .Where(character =>
                    character.AccountId == accountId &&
                    string.Equals(character.Name, characterName, StringComparison.OrdinalIgnoreCase))
                .Select(character => character.Id)
                .ToHashSet();
            var removed = db.Characters.RemoveAll(c =>
                c.AccountId == accountId &&
                string.Equals(c.Name, characterName, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                db.CharacterTalents.RemoveAll(talent => removedCharacterIds.Contains(talent.CharacterId));
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
            if (equipmentEntry == "[]"
                || kitBagSlot is < 0 or >= 96
                || !KitBagSlots.GetItem(character.KitBag, kitBagSlot).IsEmpty)
            {
                return Clone(character);
            }

            character.Equipment = EquipmentSlots.ClearSlot(character.Equipment, character.Profession, equipmentSlot);
            character.KitBag = KitBagSlots.SetSlot(character.KitBag, kitBagSlot, equipmentEntry);

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
        CancellationToken cancellationToken = default,
        bool requireEmptyEquipmentSlot = false)
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
            if (item.IsEmpty
                || kitBagEntry == "[]"
                || !EquipmentSlots.TryGetAuthoritativeSlot(item.Id, out var defaultEquipmentSlot))
            {
                return null;
            }

            var equipmentSlot = EquipmentSlots.ResolveSlotForItem(
                item.Id,
                requestedEquipmentSlot,
                character.Equipment,
                character.Profession,
                defaultEquipmentSlot);
            if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot))
            {
                return null;
            }

            var previousEquipmentEntry = EquipmentSlots.GetEntry(character.Equipment, character.Profession, equipmentSlot);
            if (requireEmptyEquipmentSlot && previousEquipmentEntry != "[]")
            {
                return Clone(character);
            }

            character.Equipment = EquipmentSlots.SetSlot(character.Equipment, character.Profession, equipmentSlot, kitBagEntry);
            character.KitBag = previousEquipmentEntry == "[]"
                ? KitBagSlots.ClearSlot(character.KitBag, kitBagSlot)
                : KitBagSlots.SetSlot(character.KitBag, kitBagSlot, previousEquipmentEntry);

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

    public async Task<GameCharacter?> DeleteKitBagItemAsync(
        int accountId,
        int characterId,
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

            character.KitBag = KitBagSlots.ClearSlot(character.KitBag, kitBagSlot);
            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> ClearKitBagAsync(
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
                return null;
            }

            character.KitBag = GameDefaults.EmptyKitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KitBagItemGrantResult> AddForgingMaterialAsync(
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        if (!DeveloperGrantMaterialCatalog.TryResolve(itemId, out var material))
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemId),
                "Item is not in the developer material allowlist.");
        }

        if (quantity is < 1 or > KitBagItemGrantPlanner.MaximumQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c => c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return new KitBagItemGrantResult(KitBagItemGrantStatus.CharacterNotFound, null);
            }

            if (!KitBagItemGrantPlanner.TryAdd(
                    character.KitBag,
                    itemId,
                    quantity,
                    material.StackCap,
                    material.GrantedBound,
                    out var updatedKitBag))
            {
                return new KitBagItemGrantResult(
                    KitBagItemGrantStatus.InsufficientCapacity,
                    Clone(character));
            }

            character.KitBag = updatedKitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return new KitBagItemGrantResult(KitBagItemGrantStatus.Added, Clone(character));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ForgeTransactionResult> ForgeEquipmentAsync(
        int accountId,
        int characterId,
        ForgeTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c =>
                c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return new ForgeTransactionResult(
                    ForgeTransactionStatus.CharacterNotFound,
                    null,
                    0,
                    0,
                    0,
                    CompactItemEntry.Empty,
                    CompactItemEntry.Empty,
                    "Character was not found.");
            }

            var equipmentBefore = request is not null &&
                                  request.Equipment.KitBagSlot is >= 0 and < 96
                ? KitBagSlots.GetItem(character.KitBag, request.Equipment.KitBagSlot)
                : CompactItemEntry.Empty;
            if (!ForgePersistencePlanner.TryCreate(
                    character.KitBag,
                    character.Silver,
                    request,
                    System.Security.Cryptography.RandomNumberGenerator.GetInt32(100),
                    out var plan,
                    out var rejectionStatus,
                    out var rejectionReason))
            {
                return new ForgeTransactionResult(
                    rejectionStatus,
                    Clone(character),
                    0,
                    0,
                    0,
                    equipmentBefore,
                    equipmentBefore,
                    rejectionReason);
            }

            character.KitBag = plan!.UpdatedKitBag;
            character.Silver = plan.UpdatedSilver;
            await SaveUnsafeAsync(db, cancellationToken);

            return new ForgeTransactionResult(
                plan.Succeeded
                    ? ForgeTransactionStatus.Succeeded
                    : ForgeTransactionStatus.FailedRoll,
                Clone(character),
                (int)plan.Calculation.Operation,
                plan.Calculation.SuccessProbability,
                plan.Calculation.SilverCost,
                equipmentBefore,
                plan.Succeeded
                    ? plan.Calculation.SuccessEquipment
                    : plan.Calculation.FailureEquipment);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GearEnhancementTransactionResult> EnhanceGearAsync(
        int accountId,
        int characterId,
        GearEnhancementRequest request,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c =>
                c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return new GearEnhancementTransactionResult(null, null);
            }

            var enhancement = GearEnhancementPlanner.Create(character.KitBag, request);
            if (!enhancement.Committed)
            {
                return new GearEnhancementTransactionResult(
                    enhancement,
                    Clone(character));
            }

            character.KitBag = enhancement.UpdatedKitBag;
            await SaveUnsafeAsync(db, cancellationToken);

            return new GearEnhancementTransactionResult(
                enhancement,
                Clone(character));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GearMentorTransactionResult> ProcessGearMentorAsync(
        int accountId,
        int characterId,
        GearMentorRequest request,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(c =>
                c.AccountId == accountId && c.Id == characterId);
            if (character is null)
            {
                return new GearMentorTransactionResult(null, null);
            }

            var result = GearMentorPlanner.Create(
                character.KitBag,
                character.Level,
                request);
            if (!result.Committed)
            {
                return new GearMentorTransactionResult(result, Clone(character));
            }

            character.KitBag = result.UpdatedKitBag;
            await SaveUnsafeAsync(db, cancellationToken);
            return new GearMentorTransactionResult(result, Clone(character));
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
