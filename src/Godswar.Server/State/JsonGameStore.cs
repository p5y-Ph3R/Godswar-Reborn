using System.Collections.Concurrent;
using System.Text.Json;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Game;

namespace Godswar.Server.State;

internal sealed partial class JsonGameStore :
    IGameStore,
    ICharacterSnapshotReader,
    IAccountCredentialStore,
    IAccountDirectory,
    IAccountPresenceWriter,
    ILegacyAccountLoginStore,
    ISemanticGatewayCharacterRouteReader
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

    public Task MarkAccountOfflineAsync(int accountId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default) =>
        SaveCharacterPositionCoreAsync(
            accountId,
            characterId,
            currentMap,
            positionX,
            positionZ,
            revision: null,
            cancellationToken);

    internal Task SaveCharacterPositionCheckpointAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        long revision,
        CancellationToken cancellationToken = default) =>
        SaveCharacterPositionCoreAsync(
            accountId,
            characterId,
            currentMap,
            positionX,
            positionZ,
            revision,
            cancellationToken);

    private async Task SaveCharacterPositionCoreAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        long? revision,
        CancellationToken cancellationToken)
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
            if (revision.HasValue &&
                revision.Value <= character.PositionRevision)
            {
                return;
            }

            character.CurrentMap = currentMap;
            character.PositionX = positionX;
            character.PositionZ = positionZ;
            if (revision.HasValue)
            {
                character.PositionRevision = revision.Value;
            }
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

}
