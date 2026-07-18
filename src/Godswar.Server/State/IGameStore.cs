namespace Godswar.Server.State;

internal interface IGameStore : IAsyncDisposable
{
    Task EnsureSeedDataAsync(CancellationToken cancellationToken = default);

    Task<GameAccount> LoginOrCreateAccountAsync(string username, string password, CancellationToken cancellationToken = default);

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
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> MoveKitBagItemAsync(
        int accountId,
        int characterId,
        int sourceSlot,
        int destinationSlot,
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
