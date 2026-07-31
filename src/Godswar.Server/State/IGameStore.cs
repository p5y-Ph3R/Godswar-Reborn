using Godswar.Server.Application.Characters;

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

    Task<ZodiacSkillGridSelectionResult?> SelectZodiacSkillGridAsync(
        int accountId,
        int characterId,
        int gridIndex,
        int selectedSkillKind,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(int accountId, CancellationToken cancellationToken = default);

    Task<GameCharacter?> GetFirstCharacterAsync(int accountId, CancellationToken cancellationToken = default);

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
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TalentState>> GetTalentStatesAsync(
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

}
