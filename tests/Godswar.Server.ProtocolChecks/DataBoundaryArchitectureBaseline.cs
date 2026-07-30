namespace Godswar.Server.ProtocolChecks;

internal readonly record struct LegacyStoreCallAllowance(
    string Path,
    string Member,
    int Count);

internal readonly record struct ReferenceAllowance(
    string Path,
    int Count);

internal sealed record DataBoundaryBaselineSnapshot(
    IReadOnlyList<LegacyStoreCallAllowance> StoreCalls,
    IReadOnlyList<ReferenceAllowance> StoreFieldReferences,
    IReadOnlyList<ReferenceAllowance> StoreParameterReferences,
    IReadOnlyList<ReferenceAllowance> StoreTypeReferences,
    IReadOnlyList<ReferenceAllowance> LegacyNpgsqlReferences,
    IReadOnlyList<ReferenceAllowance> StateToGameUsings,
    IReadOnlyList<string> GameStoreMethods);

internal static class DataBoundaryArchitectureBaseline
{
    public static readonly LegacyStoreCallAllowance[] StoreCalls =
    [
        new("Game/GameClientHandler.Backhaul.cs", "SaveCharacterVitalsAsync", 1),
        new("Game/GameClientHandler.CombatArea.cs", "SaveCharacterVitalsAsync", 1),
        new("Game/GameClientHandler.CombatEcsSkill.cs", "SaveCharacterVitalsAsync", 2),
        new("Game/GameClientHandler.CombatSkill.cs", "SaveCharacterVitalsAsync", 2),
        new("Game/GameClientHandler.CombatStunStatus.cs", "SaveCharacterVitalsAsync", 2),
        new("Game/GameClientHandler.cs", "MarkAccountOfflineAsync", 1),
        new("Game/GameClientHandler.DeveloperCommands.cs", "AddDeveloperMountAsync", 1),
        new("Game/GameClientHandler.DeveloperCommands.cs", "AddForgingMaterialAsync", 1),
        new("Game/GameClientHandler.DeveloperCommands.cs", "ClearKitBagAsync", 1),
        new("Game/GameClientHandler.Equipment.cs", "DeleteKitBagItemAsync", 1),
        new("Game/GameClientHandler.Equipment.cs", "MoveKitBagItemAsync", 1),
        new("Game/GameClientHandler.Equipment.cs", "MoveKitBagToEquipmentAsync", 1),
        new("Game/GameClientHandler.Forging.cs", "ForgeEquipmentAsync", 1),
        new("Game/GameClientHandler.GearEnhancer.cs", "EnhanceGearAsync", 1),
        new("Game/GameClientHandler.GearMentor.cs", "ProcessGearMentorAsync", 1),
        new("Game/GameClientHandler.InventoryActions.cs", "MoveEquipmentToKitBagAsync", 1),
        new("Game/GameClientHandler.InventoryActions.cs", "UpgradeTalentAsync", 1),
        new("Game/GameClientHandler.LoginWorldEntry.cs", "CreateCharacterAsync", 1),
        new("Game/GameClientHandler.LoginWorldEntry.cs", "DeleteCharacterAsync", 1),
        new("Game/GameClientHandler.LoginWorldEntry.cs", "FindAccountByIdAsync", 1),
        new("Game/GameClientHandler.LoginWorldEntry.cs", "FindAccountByUsernameAsync", 1),
        new("Game/GameClientHandler.LoginWorldEntry.cs", "GetActiveWorldBossRespawnAsync", 1),
        new("Game/GameClientHandler.MapTransitions.cs", "SaveCharacterPositionAsync", 2),
        new("Game/GameClientHandler.MovementCombat.cs", "SaveCharacterPositionAsync", 1),
        new("Game/GameClientHandler.MovementCombat.cs", "SaveCharacterVitalsAsync", 1),
        new(
            "Game/GameClientHandler.LegacyHolyStone.cs",
            "ApplyWeaponHolyStoneAsync",
            1),
        new("Game/GameClientHandler.PetEggs.cs", "GetOwnedPetsAsync", 1),
        new("Game/GameClientHandler.PetEggs.cs", "HatchPetEggAsync", 1),
        new("Game/GameClientHandler.PetLevel.cs", "UpgradePetLevelAsync", 1),
        new("Game/GameClientHandler.Pets.cs", "GetOwnedPetsAsync", 1),
        new("Game/GameClientHandler.Pets.cs", "TransitionPetPresenceAsync", 1),
        new("Game/GameClientHandler.PlayerVisibility.cs", "GetCharacterStatsAsync", 1),
        new("Game/GameClientHandler.PlayerVisibility.cs", "SaveCharacterPositionAsync", 1),
        new("Game/GameClientHandler.Progression.cs", "ActivateWorldBossAreaAsync", 1),
        new("Game/GameClientHandler.Progression.cs", "ApplyMonsterKillRewardAsync", 1),
        new("Game/GameClientHandler.Progression.cs", "GetCharacterStatsAsync", 1),
        new("Game/GameClientHandler.Progression.cs", "GetSkillStatesAsync", 1),
        new(
            "Game/GameClientHandler.RealtimeMovement.Persistence.cs",
            "SaveCharacterPositionAsync",
            1),
        new("Game/GameClientHandler.Ride.cs", "SaveCharacterVitalsAsync", 1),
        new("Game/GameSessionRegistry.BackgroundLoops.cs", "SaveCharacterVitalsAsync", 1),
        new("Game/GameSessionRegistry.MonsterAttacks.cs", "SaveCharacterVitalsAsync", 1),
        new("Game/GameSessionRegistry.MonsterAttacksEcs.cs", "SaveCharacterVitalsAsync", 1),
        new("Game/GameSessionRegistry.MountStatus.cs", "SaveCharacterVitalsAsync", 2),
        new("Game/GameSessionRegistry.PlayerRecoveryEcs.cs", "SaveCharacterVitalsAsync", 1),
        new("Game/GameSessionRegistry.Progression.cs", "ApplyZodiacOnlineTimeAsync", 1),
        new(
            "Game/GameSessionRegistry.Progression.cs",
            "ConsumeCharacterBoostOnlineTimeAsync",
            1),
        new("Game/GameSessionRegistry.Progression.cs", "GetExperienceBoostStateAsync", 1),
        new("Game/GameSessionRegistry.Progression.cs", "UpgradeZodiacLevelAsync", 2),
        new(
            "Game/GameSessionRegistry.ZodiacSkillGrids.cs",
            "ActivateZodiacSkillGridAsync",
            2),
        new(
            "Game/GameSessionRegistry.ZodiacSkillGrids.cs",
            "UpgradeZodiacSkillGridAsync",
            2),
        new(
            "Game/GameSessionRegistry.ZodiacSkillGrids.cs",
            "SelectZodiacSkillGridAsync",
            2),
        new("Game/LoginClientHandler.cs", "LoginOrCreateAccountAsync", 1),
        new(
            "Security/Authentication/AccountAuthenticationService.cs",
            "FindAccountCredentialAsync",
            4),
        new(
            "Security/Authentication/AccountAuthenticationService.cs",
            "MarkAccountOnlineAsync",
            3),
        new(
            "Security/Authentication/AccountAuthenticationService.cs",
            "TryCreateAccountWithCredentialAsync",
            1),
        new(
            "Security/Authentication/AccountAuthenticationService.cs",
            "TryReplaceAccountCredentialAsync",
            2)
    ];

    public static readonly ReferenceAllowance[] StoreTypeReferences =
    [
        new("Game/GameClientHandler.cs", 2),
        new("Game/GameSessionRegistry.cs", 2),
        new("Game/GameSessionRegistry.PlayerRuntimeEcs.cs", 1),
        new("Game/LoginClientHandler.cs", 2),
        new("Program.cs", 1),
        new("Security/Authentication/AccountAuthenticationService.cs", 2)
    ];

    public static readonly ReferenceAllowance[] StoreFieldReferences =
    [
        new("Game/GameClientHandler.Backhaul.cs", 1),
        new("Game/GameClientHandler.CombatArea.cs", 1),
        new("Game/GameClientHandler.CombatEcsSkill.cs", 2),
        new("Game/GameClientHandler.CombatSkill.cs", 2),
        new("Game/GameClientHandler.CombatStunStatus.cs", 2),
        new("Game/GameClientHandler.cs", 3),
        new("Game/GameClientHandler.DeveloperCommands.cs", 3),
        new("Game/GameClientHandler.Equipment.cs", 3),
        new("Game/GameClientHandler.Forging.cs", 1),
        new("Game/GameClientHandler.GearEnhancer.cs", 1),
        new("Game/GameClientHandler.GearMentor.cs", 1),
        new("Game/GameClientHandler.InventoryActions.cs", 2),
        new("Game/GameClientHandler.LoginWorldEntry.cs", 5),
        new("Game/GameClientHandler.MapTransitions.cs", 2),
        new("Game/GameClientHandler.MovementCombat.cs", 2),
        new("Game/GameClientHandler.LegacyHolyStone.cs", 1),
        new("Game/GameClientHandler.PetEggs.cs", 2),
        new("Game/GameClientHandler.PetLevel.cs", 1),
        new("Game/GameClientHandler.Pets.cs", 2),
        new("Game/GameClientHandler.PlayerVisibility.cs", 2),
        new("Game/GameClientHandler.Progression.cs", 4),
        new("Game/GameClientHandler.RealtimeMovement.Persistence.cs", 1),
        new("Game/GameClientHandler.Ride.cs", 1),
        new("Game/GameSessionRegistry.BackgroundLoops.cs", 2),
        new("Game/GameSessionRegistry.cs", 2),
        new("Game/GameSessionRegistry.MonsterAttacks.cs", 2),
        new("Game/GameSessionRegistry.MonsterAttacksEcs.cs", 2),
        new("Game/GameSessionRegistry.MountStatus.cs", 3),
        new("Game/GameSessionRegistry.PlayerRecoveryEcs.cs", 2),
        new("Game/GameSessionRegistry.PlayerStatusMutations.cs", 1),
        new("Game/GameSessionRegistry.PlayerStatusPublishing.cs", 1),
        new("Game/GameSessionRegistry.Progression.cs", 13),
        new("Game/GameSessionRegistry.ZodiacSkillGrids.cs", 9),
        new("Game/LoginClientHandler.cs", 3),
        new("Security/Authentication/AccountAuthenticationService.cs", 12)
    ];

    public static readonly ReferenceAllowance[] StoreParameterReferences =
    [
        new("Game/GameClientHandler.cs", 2),
        new("Game/GameSessionRegistry.cs", 2),
        new("Game/GameSessionRegistry.PlayerRuntimeEcs.cs", 2),
        new("Game/LoginClientHandler.cs", 2),
        new("Program.cs", 8),
        new("Security/Authentication/AccountAuthenticationService.cs", 3)
    ];

    public static readonly ReferenceAllowance[] LegacyNpgsqlReferences =
    [
        new("Operations/ControlledHostValidationCommand.cs", 2),
        new("State/DatabaseMigrations/PostgresSchemaMigrationRunner.cs", 17),
        new("State/PostgresGameStore.Characters.cs", 2),
        new("State/PostgresGameStore.Characters.Persistence.cs", 18),
        new("State/PostgresGameStore.Crafting.cs", 7),
        new("State/PostgresGameStore.cs", 5),
        new("State/PostgresGameStore.Experience.cs", 4),
        new("State/PostgresGameStore.Inventory.Grants.cs", 9),
        new("State/PostgresGameStore.Inventory.HolyStones.cs", 11),
        new("State/PostgresGameStore.Inventory.Movement.cs", 12),
        new("State/PostgresGameStore.Inventory.Persistence.cs", 32),
        new("State/PostgresGameStore.Inventory.Projection.cs", 26),
        new("State/PostgresGameStore.PetEggs.Audit.cs", 4),
        new("State/PostgresGameStore.PetEggs.cs", 10),
        new("State/PostgresGameStore.PetLevel.cs", 16),
        new("State/PostgresGameStore.PetLevelStats.cs", 8),
        new("State/PostgresGameStore.PetPresence.cs", 18),
        new("State/PostgresGameStore.PetPresenceAudit.cs", 10),
        new("State/PostgresGameStore.Pets.cs", 6),
        new("State/PostgresGameStore.Progression.cs", 14),
        new("State/PostgresGameStore.Seeding.Items.cs", 25),
        new("State/PostgresGameStore.Seeding.SkillsAndNpcs.cs", 30),
        new("State/PostgresGameStore.Seeding.World.cs", 18),
        new("State/PostgresGameStore.SkillsAndTalents.cs", 7),
        new("State/PostgresGameStore.ZodiacSkillGrids.cs", 20)
    ];

    public static readonly ReferenceAllowance[] StateToGameUsings =
    [
        new("State/JsonGameStore.cs", 1),
        new("State/PostgresGameStore.Characters.cs", 1),
        new("State/PostgresGameStore.Characters.Persistence.cs", 1),
        new("State/PostgresGameStore.Crafting.cs", 1),
        new("State/PostgresGameStore.cs", 1),
        new("State/PostgresGameStore.Experience.cs", 1),
        new("State/PostgresGameStore.Inventory.Grants.cs", 1),
        new("State/PostgresGameStore.Inventory.HolyStones.cs", 1),
        new("State/PostgresGameStore.Inventory.Movement.cs", 1),
        new("State/PostgresGameStore.Inventory.Persistence.cs", 1),
        new("State/PostgresGameStore.Inventory.Projection.cs", 1),
        new("State/PostgresGameStore.PetEggs.Audit.cs", 1),
        new("State/PostgresGameStore.PetEggs.cs", 1),
        new("State/PostgresGameStore.Progression.cs", 1),
        new("State/PostgresGameStore.Seeding.Items.cs", 1),
        new("State/PostgresGameStore.Seeding.SkillsAndNpcs.cs", 1),
        new("State/PostgresGameStore.Seeding.World.cs", 1),
        new("State/PostgresGameStore.SkillsAndTalents.cs", 1),
    ];

    public static readonly string[] GameStoreMethods =
    [
        "ActivateWorldBossAreaAsync",
        "ActivateZodiacSkillGridAsync",
        "AddDeveloperMountAsync",
        "AddForgingMaterialAsync",
        "AddZodiacAccumulationAsync",
        "ApplyMonsterKillRewardAsync",
        "ApplyWeaponHolyStoneAsync",
        "ApplyZodiacOnlineTimeAsync",
        "ClearKitBagAsync",
        "ConsumeCharacterBoostOnlineTimeAsync",
        "CreateCharacterAsync",
        "DeleteCharacterAsync",
        "DeleteKitBagItemAsync",
        "EnhanceGearAsync",
        "EnsureSeedDataAsync",
        "FindAccountByIdAsync",
        "FindAccountByUsernameAsync",
        "FindAccountCredentialAsync",
        "ForgeEquipmentAsync",
        "GetActiveWorldBossRespawnAsync",
        "GetCharactersAsync",
        "GetCharacterStatsAsync",
        "GetExperienceBoostStateAsync",
        "GetFirstCharacterAsync",
        "GetOwnedPetsAsync",
        "GetSkillStatesAsync",
        "GetTalentStatesAsync",
        "HatchPetEggAsync",
        "LoginOrCreateAccountAsync",
        "MarkAccountOfflineAsync",
        "MarkAccountOnlineAsync",
        "MoveEquipmentToKitBagAsync",
        "MoveKitBagItemAsync",
        "MoveKitBagToEquipmentAsync",
        "ProcessGearMentorAsync",
        "SaveCharacterPositionAsync",
        "SaveCharacterVitalsAsync",
        "SelectZodiacSkillGridAsync",
        "TransitionPetPresenceAsync",
        "TryCreateAccountWithCredentialAsync",
        "TryReplaceAccountCredentialAsync",
        "UpgradePetLevelAsync",
        "UpgradeTalentAsync",
        "UpgradeZodiacLevelAsync",
        "UpgradeZodiacSkillGridAsync"
    ];

    public const string GameStoreSignatureSha256 =
        "9368203D1026E96914519DB63712744842103DEC10FAB521FE597A7E5A80C791";

    public static DataBoundaryBaselineSnapshot Snapshot { get; } =
        new(
            StoreCalls,
            StoreFieldReferences,
            StoreParameterReferences,
            StoreTypeReferences,
            LegacyNpgsqlReferences,
            StateToGameUsings,
            GameStoreMethods);
}
