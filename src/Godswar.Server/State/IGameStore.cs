namespace Godswar.Server.State;

internal sealed record GearEnhancementTransactionResult(
    GearEnhancementResult? Enhancement,
    GameCharacter? Character)
{
    public bool CharacterFound => Character is not null;

    public bool Committed => Enhancement?.Committed == true;
}

internal sealed record GearMentorTransactionResult(
    GearMentorResult? Result,
    GameCharacter? Character)
{
    public bool CharacterFound => Character is not null;

    public bool Committed => Result?.Committed == true;
}

internal interface IGameStore : IAsyncDisposable
{
    Task EnsureSeedDataAsync(CancellationToken cancellationToken = default);

    Task<GameAccount> LoginOrCreateAccountAsync(string username, string password, CancellationToken cancellationToken = default);

    Task<StoredAccountCredential?> FindAccountCredentialAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<GameAccount?> FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task<GameAccount?> FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<GameAccount?> TryCreateAccountWithCredentialAsync(
        string username,
        string versionedVerifier,
        CancellationToken cancellationToken = default);

    Task<bool> TryReplaceAccountCredentialAsync(
        int accountId,
        string expectedVerifier,
        string versionedVerifier,
        CancellationToken cancellationToken = default);

    Task MarkAccountOnlineAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task MarkAccountOfflineAsync(int accountId, CancellationToken cancellationToken = default);

    Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default);

    Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        long vitalsRevision,
        CancellationToken cancellationToken = default);

    Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
        int accountId,
        int characterId,
        int experience,
        int talentExperience,
        CancellationToken cancellationToken = default);

    Task<ZodiacAccumulationResult?> AddZodiacAccumulationAsync(
        int accountId,
        int characterId,
        int experienceGainX100,
        int talentExperienceGainX100,
        CancellationToken cancellationToken = default);

    Task<ZodiacEnergyAccrualResult?> ApplyZodiacOnlineTimeAsync(
        int accountId,
        int characterId,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil,
        ZodiacEnergyPolicy policy,
        CancellationToken cancellationToken = default);

    Task<ZodiacLevelUpgradeResult?> UpgradeZodiacLevelAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task<ZodiacSkillGridActivationResult?> ActivateZodiacSkillGridAsync(
        int accountId,
        int characterId,
        int gridIndex,
        CancellationToken cancellationToken = default);

    Task<ZodiacSkillGridUpgradeResult?> UpgradeZodiacSkillGridAsync(
        int accountId,
        int characterId,
        int gridIndex,
        CancellationToken cancellationToken = default);

    Task<ExperienceBoostState> GetExperienceBoostStateAsync(
        int accountId,
        int characterId,
        byte camp,
        byte mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task ConsumeCharacterBoostOnlineTimeAsync(
        int accountId,
        int characterId,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil,
        CancellationToken cancellationToken = default);

    Task<FactionAreaExperienceControl?> ActivateWorldBossAreaAsync(
        short mapId,
        string bossTemplateKey,
        byte controllingCamp,
        DateTimeOffset killedAt,
        string deathToken,
        CancellationToken cancellationToken = default);

    Task<WorldBossRespawnState?> GetActiveWorldBossRespawnAsync(
        short mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(int accountId, CancellationToken cancellationToken = default);

    Task<GameCharacter?> GetFirstCharacterAsync(int accountId, CancellationToken cancellationToken = default);

    Task<CharacterStats?> GetCharacterStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task<GameCharacter> CreateCharacterAsync(int accountId, GameCharacter character, CancellationToken cancellationToken = default);

    Task<bool> DeleteCharacterAsync(int accountId, string characterName, CancellationToken cancellationToken = default);

    Task<GameCharacter?> MoveEquipmentToKitBagAsync(
        int accountId,
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> MoveKitBagToEquipmentAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        int requestedEquipmentSlot,
        CancellationToken cancellationToken = default,
        bool requireEmptyEquipmentSlot = false);

    Task<GameCharacter?> MoveKitBagItemAsync(
        int accountId,
        int characterId,
        int sourceSlot,
        int destinationSlot,
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> DeleteKitBagItemAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> ClearKitBagAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task<KitBagItemGrantResult> AddForgingMaterialAsync(
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        CancellationToken cancellationToken = default);

    Task<KitBagItemGrantResult> AddDeveloperMountAsync(
        int accountId,
        int characterId,
        uint itemId,
        CancellationToken cancellationToken = default);

    Task<ForgeTransactionResult> ForgeEquipmentAsync(
        int accountId,
        int characterId,
        ForgeTransactionRequest request,
        CancellationToken cancellationToken = default);

    Task<GearEnhancementTransactionResult> EnhanceGearAsync(
        int accountId,
        int characterId,
        GearEnhancementRequest request,
        CancellationToken cancellationToken = default);

    Task<GearMentorTransactionResult> ProcessGearMentorAsync(
        int accountId,
        int characterId,
        GearMentorRequest request,
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> ApplyWeaponHolyStoneAsync(
        int accountId,
        int characterId,
        HolyStoneOperation operation,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TalentState>> GetTalentStatesAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SkillState>> GetSkillStatesAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task<TalentUpgradeResult?> UpgradeTalentAsync(
        int accountId,
        int characterId,
        int talentId,
        int clientRank,
        int clientTalentPoints,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CapturedNpcSpawn>> GetCapturedNpcSpawnsAsync(
        short mapId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NpcSpawnDefinition>> GetNpcSpawnDefinitionsAsync(
        short mapId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CapturedMonsterSpawn>> GetCapturedMonsterSpawnsAsync(
        short mapId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<byte[]>> GetEnterSyncPacketsAsync(CancellationToken cancellationToken = default);
}
