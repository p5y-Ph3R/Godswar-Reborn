namespace Godswar.Server.ProtocolChecks;

internal enum B20LegacyDependencyKind : byte
{
    BroadStoreType = 1,
    PostgresBroadStoreType = 2,
    JsonStoreType = 3,
    JsonProviderBranch = 4,
    JsonSnapshotBranch = 5,
    JsonWorldContentFallback = 6,
    LegacyCheckpointAdapter = 7,
    LegacyGatewayAdapter = 8,
    JsonStateAggregate = 9,
    JsonCheckpointCall = 10,
    JsonToolConfiguration = 11,
    LegacySchemaBootstrap = 12,
    LegacyLoadoutProjection = 13,
    CaptureBackedContentTable = 14,
    GeneratedSeedConsumer = 15,
    LegacyDockerInitMount = 16
}

internal readonly record struct B20LegacyDependencyAllowance(
    B20LegacyDependencyKind Kind,
    string Path,
    int Count);

internal sealed record B20LegacyPersistenceBaselineSnapshot(
    bool RetirementComplete,
    IReadOnlyList<B20LegacyDependencyAllowance> References,
    IReadOnlyList<string> JsonProviderConfigurations);

internal static class B20LegacyPersistenceBaseline
{
    public const bool RetirementComplete = false;
    public const int ExpectedBroadStoreCalls = 43;
    public const int ExpectedJsonSpecificCalls = 1;
    public const int ExpectedReadCalls = 8;
    public const int ExpectedMutationOrMixedCalls = 35;
    public const int ExpectedBootstrapCalls = 1;
    public const int ExpectedJsonStoreImplementationFiles = 10;
    public const int ExpectedPostgresStoreImplementationFiles = 31;

    public static readonly B20LegacyDependencyAllowance[] References =
    [
        // Broad IGameStore contract and consumers.
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/GameClientHandlerFactory.cs", 1),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/Program.cs", 1),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/Game/GameClientHandler.Construction.cs", 1),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/Game/GameClientHandler.cs", 1),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/Game/GameSessionRegistry.cs", 2),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/Game/GameSessionRegistry.PlayerRuntimeEcs.cs", 1),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/State/IGameStore.cs", 1),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/State/JsonGameStore.cs", 1),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/State/LegacyCharacterCheckpointStore.cs", 2),
        A(B20LegacyDependencyKind.BroadStoreType, "src/Godswar.Server/State/PostgresGameStore.cs", 1),

        // Broad PostgreSQL store implementation. New persistence belongs in
        // focused Infrastructure adapters, not another partial here.
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/Program.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Accounts.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.CharacterCreationEconomyBaseline.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.CharacterLifecycle.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.CharacterLookup.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Characters.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Characters.Persistence.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Crafting.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.cs", 2),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Experience.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Inventory.Grants.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Inventory.HolyStones.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Inventory.Movement.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Inventory.Persistence.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Inventory.Projection.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetAddedSavvyPolicy.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetDurabilityGuards.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetEggs.Audit.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetEggs.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetGrowthPolicy.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetInitialSavvyPolicy.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetLevel.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetLevelStats.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetPresence.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.PetPresenceAudit.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Pets.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Progression.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Seeding.Items.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Seeding.SkillsAndNpcs.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.Seeding.World.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.SkillsAndTalents.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.ZodiacSkillGrids.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "tools/Godswar.Server.SecureSmoke/TransientAccountFixture.cs", 3),

        // JSON authority and selection paths.
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/Program.cs", 2),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.Accounts.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.CharacterLifecycle.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.CharacterSnapshots.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.cs", 2),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.Inventory.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.Persistence.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.Pets.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.Progression.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.Zodiac.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/JsonGameStore.ZodiacSkillGrids.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/LegacyCharacterCheckpointStore.cs", 1),
        A(B20LegacyDependencyKind.JsonStoreType, "src/Godswar.Server/State/LegacySemanticGatewayDataSession.cs", 1),
        A(B20LegacyDependencyKind.JsonProviderBranch, "src/Godswar.Server/Program.cs", 2),
        A(B20LegacyDependencyKind.JsonProviderBranch, "src/Godswar.Server/ServerRuntimeProfilePolicy.cs", 3),
        A(B20LegacyDependencyKind.JsonProviderBranch, "src/Godswar.Server/ServerWorldContentComposition.cs", 1),
        A(B20LegacyDependencyKind.JsonProviderBranch, "src/Godswar.Server/ServerStartupCommandDispatcher.cs", 1),
        A(B20LegacyDependencyKind.JsonSnapshotBranch, "src/Godswar.Server/Program.cs", 1),
        A(B20LegacyDependencyKind.JsonSnapshotBranch, "src/Godswar.Server/Application/Characters/MeasuredCharacterSnapshotReader.cs", 1),
        A(B20LegacyDependencyKind.JsonWorldContentFallback, "src/Godswar.Server/ServerWorldContentComposition.cs", 1),
        A(B20LegacyDependencyKind.JsonWorldContentFallback, "src/Godswar.Server/Infrastructure/WorldContent/GeneratedWorldContentReaderLoader.cs", 1),
        A(B20LegacyDependencyKind.LegacyCheckpointAdapter, "src/Godswar.Server/Program.cs", 1),
        A(B20LegacyDependencyKind.LegacyCheckpointAdapter, "src/Godswar.Server/State/LegacyCharacterCheckpointStore.cs", 1),
        A(B20LegacyDependencyKind.JsonStateAggregate, "src/Godswar.Server/State/GameDatabase.cs", 1),
        A(B20LegacyDependencyKind.JsonStateAggregate, "src/Godswar.Server/State/JsonGameStore.Accounts.cs", 1),
        A(B20LegacyDependencyKind.JsonStateAggregate, "src/Godswar.Server/State/JsonGameStore.CharacterLifecycle.cs", 1),
        A(B20LegacyDependencyKind.JsonStateAggregate, "src/Godswar.Server/State/JsonGameStore.CharacterSnapshots.cs", 3),
        A(B20LegacyDependencyKind.JsonStateAggregate, "src/Godswar.Server/State/JsonGameStore.cs", 1),
        A(B20LegacyDependencyKind.JsonStateAggregate, "src/Godswar.Server/State/JsonGameStore.Persistence.cs", 6),
        A(B20LegacyDependencyKind.JsonCheckpointCall, "src/Godswar.Server/State/LegacyCharacterCheckpointStore.cs", 1),
        A(B20LegacyDependencyKind.JsonToolConfiguration, "tools/Godswar.Server.B18CSmoke/SmokeWorkspace.cs", 2),

        // Fresh-install bootstrap and compatibility projections.
        A(B20LegacyDependencyKind.LegacySchemaBootstrap, "src/Godswar.Server/Godswar.Server.csproj", 12),
        A(B20LegacyDependencyKind.LegacySchemaBootstrap, "src/Godswar.Server/State/DatabaseMigrations/LegacySchemaBootstrap.cs", 8),
        A(B20LegacyDependencyKind.LegacySchemaBootstrap, "src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationRunner.cs", 1),
        A(B20LegacyDependencyKind.LegacyDockerInitMount, "docker-compose.yml", 1),
        A(B20LegacyDependencyKind.LegacyLoadoutProjection, "src/Godswar.Server/Infrastructure/Characters/PostgresCharacterSnapshotReader.Core.cs", 1),
        A(B20LegacyDependencyKind.LegacyLoadoutProjection, "src/Godswar.Server/State/PostgresGameStore.CharacterLookup.cs", 2),
        A(B20LegacyDependencyKind.LegacyLoadoutProjection, "src/Godswar.Server/State/PostgresGameStore.Characters.cs", 2),
        A(B20LegacyDependencyKind.LegacyLoadoutProjection, "src/Godswar.Server/State/PostgresGameStore.Inventory.Movement.cs", 2),
        A(B20LegacyDependencyKind.LegacyLoadoutProjection, "tools/SetEquippedWeapon.ps1", 1),
        A(B20LegacyDependencyKind.CaptureBackedContentTable, "src/Godswar.Server/Infrastructure/WorldContent/PostgresWorldContentReaderLoader.cs", 2),

        // Compiled seed consumers remain live. Generated declaration files
        // are intentionally excluded; consumers are not.
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/Game/MapTraversalCatalog.cs", 3),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/Game/MonsterCombatResolver.cs", 2),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/Game/WorldBossCatalog.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/Infrastructure/WorldContent/GeneratedWorldContentReaderLoader.cs", 2),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/DeveloperMountCatalog.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/EquipmentEligibility.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/EquipmentSlots.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/GearEnhancementPlanner.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/GearMentorPlanner.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/JsonGameStore.CharacterSnapshots.cs", 2),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/JsonGameStore.Progression.cs", 3),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/JsonGameStore.ZodiacSkillGrids.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/MountCatalog.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/NpcSpawnDefinition.cs", 6),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/PostgresGameStore.Seeding.Items.cs", 2),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/PostgresGameStore.Seeding.SkillsAndNpcs.cs", 10),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/PostgresGameStore.Seeding.World.cs", 7),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/State/SkillCombatCatalog.cs", 1)
    ];

    public static readonly string[] JsonProviderConfigurations =
    [
        "appsettings.backhaul-worker.example.json",
        "appsettings.json"
    ];

    public static B20LegacyPersistenceBaselineSnapshot Snapshot { get; } =
        new(
            RetirementComplete,
            References,
            JsonProviderConfigurations);

    private static B20LegacyDependencyAllowance A(
        B20LegacyDependencyKind kind,
        string path,
        int count) => new(kind, path, count);
}
