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
    public const int ExpectedBroadStoreCalls = 24;
    public const int ExpectedJsonSpecificCalls = 0;
    public const int ExpectedReadCalls = 0;
    public const int ExpectedMutationOrMixedCalls = 24;
    public const int ExpectedBootstrapCalls = 0;
    public const int ExpectedJsonStoreImplementationFiles = 0;
    public const int ExpectedPostgresStoreImplementationFiles = 28;

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
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.SkillsAndTalents.cs", 1),
        A(B20LegacyDependencyKind.PostgresBroadStoreType, "src/Godswar.Server/State/PostgresGameStore.ZodiacSkillGrids.cs", 1),
        // Fresh-install bootstrap and compatibility projections.
        A(B20LegacyDependencyKind.LegacySchemaBootstrap, "src/Godswar.Server/Godswar.Server.csproj", 12),
        A(B20LegacyDependencyKind.LegacySchemaBootstrap, "src/Godswar.Server/State/DatabaseMigrations/LegacySchemaBootstrap.cs", 8),
        A(B20LegacyDependencyKind.LegacySchemaBootstrap, "src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationRunner.cs", 1),
        // Generated declarations are excluded only by exact path. These exact
        // reviewed publisher files are the remaining compiled consumers.
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/Infrastructure/Database/PostgresSkillTimingBaselinePublisher.cs", 1),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/Infrastructure/Items/PostgresItemTemplateBaselinePublisher.MutableAuthority.cs", 2),
        A(B20LegacyDependencyKind.GeneratedSeedConsumer, "src/Godswar.Server/Infrastructure/Items/PostgresItemTemplateBaselinePublisher.Policy.cs", 3)
    ];

    public static readonly string[] JsonProviderConfigurations = [];

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
