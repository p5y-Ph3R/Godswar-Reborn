using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal abstract class GameStoreTestStub : IGameStore
{
    public virtual Task EnsureSeedDataAsync(
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public virtual Task<GameAccount> LoginOrCreateAccountAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<StoredAccountCredential?> FindAccountCredentialAsync(
        string username,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameAccount?> FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameAccount?> FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameAccount?> TryCreateAccountWithCredentialAsync(
        string username,
        string versionedVerifier,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<bool> TryReplaceAccountCredentialAsync(
        int accountId,
        string expectedVerifier,
        string versionedVerifier,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task MarkAccountOnlineAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task MarkAccountOfflineAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        long vitalsRevision,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<CharacterProgressionResult?>
        ApplyMonsterKillRewardAsync(
            int accountId,
            int characterId,
            int experience,
            int talentExperience,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacAccumulationResult?>
        AddZodiacAccumulationAsync(
            int accountId,
            int characterId,
            int experienceGainX100,
            int talentExperienceGainX100,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacEnergyAccrualResult?>
        ApplyZodiacOnlineTimeAsync(
            int accountId,
            int characterId,
            DateTimeOffset onlineFrom,
            DateTimeOffset onlineUntil,
            ZodiacEnergyPolicy policy,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacLevelUpgradeResult?>
        UpgradeZodiacLevelAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacSkillGridActivationResult?>
        ActivateZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacSkillGridUpgradeResult?>
        UpgradeZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ExperienceBoostState>
        GetExperienceBoostStateAsync(
            int accountId,
            int characterId,
            byte camp,
            byte mapId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task ConsumeCharacterBoostOnlineTimeAsync(
        int accountId,
        int characterId,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<FactionAreaExperienceControl?>
        ActivateWorldBossAreaAsync(
            short mapId,
            string bossTemplateKey,
            byte controllingCamp,
            DateTimeOffset killedAt,
            string deathToken,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<WorldBossRespawnState?>
        GetActiveWorldBossRespawnAsync(
            short mapId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyList<GameCharacter>>
        GetCharactersAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> GetFirstCharacterAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<CharacterStats?> GetCharacterStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyList<PetBootstrapSnapshot>>
        GetOwnedPetsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PetBootstrapSnapshot>>([]);

    public virtual Task<PetEggHatchResult> HatchPetEggAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            PetEggHatchResult.Rejected(
                PetEggHatchStatus.CharacterNotFound));

    public virtual Task<PetPresenceTransitionResult>
        TransitionPetPresenceAsync(
            int accountId,
            int characterId,
            long petId,
            PetPresenceOperation operation,
            CancellationToken cancellationToken = default) =>
        Task.FromResult(
            new PetPresenceTransitionResult(
                PetPresenceTransitionStatus.PetNotFound,
                petId,
                IsCarried: false,
                IsSummoned: false));

    public virtual Task<PetLevelUpgradeResult> UpgradePetLevelAsync(
        int accountId,
        int characterId,
        long petId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            PetLevelUpgradeResult.Rejected(
                PetLevelUpgradeStatus.PetNotFound,
                petId));

    public virtual Task<GameCharacter> CreateCharacterAsync(
        int accountId,
        GameCharacter character,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<bool> DeleteCharacterAsync(
        int accountId,
        string characterName,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> MoveEquipmentToKitBagAsync(
        int accountId,
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> MoveKitBagToEquipmentAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        int requestedEquipmentSlot,
        CancellationToken cancellationToken = default,
        bool requireEmptyEquipmentSlot = false) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> MoveKitBagItemAsync(
        int accountId,
        int characterId,
        int sourceSlot,
        int destinationSlot,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> DeleteKitBagItemAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> ClearKitBagAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<KitBagItemGrantResult> AddForgingMaterialAsync(
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<KitBagItemGrantResult> AddDeveloperMountAsync(
        int accountId,
        int characterId,
        uint itemId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ForgeTransactionResult> ForgeEquipmentAsync(
        int accountId,
        int characterId,
        ForgeTransactionRequest request,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GearEnhancementTransactionResult>
        EnhanceGearAsync(
            int accountId,
            int characterId,
            GearEnhancementRequest request,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GearMentorTransactionResult>
        ProcessGearMentorAsync(
            int accountId,
            int characterId,
            GearMentorRequest request,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> ApplyWeaponHolyStoneAsync(
        int accountId,
        int characterId,
        HolyStoneOperation operation,
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyList<TalentState>>
        GetTalentStatesAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyList<SkillState>>
        GetSkillStatesAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<TalentUpgradeResult?> UpgradeTalentAsync(
        int accountId,
        int characterId,
        int talentId,
        int clientRank,
        int clientTalentPoints,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual ValueTask DisposeAsync() =>
        ValueTask.CompletedTask;

    private static NotSupportedException Unsupported() =>
        new("This test store method is not used.");
}
