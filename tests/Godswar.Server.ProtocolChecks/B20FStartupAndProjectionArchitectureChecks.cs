namespace Godswar.Server.ProtocolChecks;

internal static class B20FStartupAndProjectionArchitectureChecks
{
    public const string CheckName =
        "B20F PostgreSQL startup and item-projection boundaries";

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var store = Read(
            root,
            "src/Godswar.Server/State/PostgresGameStore.cs");
        var program = Read(root, "src/Godswar.Server/Program.cs");
        var runtimeContent = Read(
            root,
            "src/Godswar.Server/ServerRuntimeContentComposition.cs");
        var gateway = Read(
            root,
            "src/Godswar.Server/Infrastructure/Gateway/" +
            "PostgresSemanticGatewayDataSession.cs");
        var projection = Read(
            root,
            "src/Godswar.Server/State/" +
            "PostgresCharacterItemProjectionSql.cs");
        var secureSmokeFixture = Read(
            root,
            "tools/Godswar.Server.SecureSmoke/" +
            "TransientAccountFixture.cs");
        var compose = Read(root, "docker-compose.yml");

        Check.True(
            !store.Contains(
                "PostgresSchemaMigrationRunner",
                StringComparison.Ordinal) &&
            !store.Contains(
                "InitializeGodswarSchemaAsync",
                StringComparison.Ordinal),
            "broad gameplay store does not own schema startup");
        Check.True(
            program.Contains(
                "PostgresSchemaStartup.InitializeAsync",
                StringComparison.Ordinal),
            "server composes schema startup before gameplay providers");
        var schema = program.IndexOf(
            "PostgresSchemaStartup.InitializeAsync",
            StringComparison.Ordinal);
        var relationalBaseline = program.IndexOf(
            "PostgresRelationalContentBaselineBootstrapper.EnsureAsync",
            StringComparison.Ordinal);
        var itemPublication = program.IndexOf(
            "ServerItemContentComposition.LoadAsync",
            StringComparison.Ordinal);
        var worldPublication = program.IndexOf(
            "ServerWorldContentComposition.TryLoadAsync",
            StringComparison.Ordinal);
        var coordination = program.IndexOf(
            "ServerRuntimeContentComposition.CreateCoordinationAsync",
            StringComparison.Ordinal);
        Check.True(
            schema >= 0 &&
            schema < relationalBaseline &&
            relationalBaseline < itemPublication &&
            itemPublication < worldPublication &&
            worldPublication < coordination,
            "startup orders schema, reviewed relational baseline, immutable " +
            "item/world publications, then distributed listeners");
        Check.True(
            runtimeContent.Contains(
                "ServerPetLearnedSkillContentComposition.LoadAsync",
                StringComparison.Ordinal) &&
            runtimeContent.Contains(
                "RuntimeContentFingerprint.Create",
                StringComparison.Ordinal) &&
            runtimeContent.Contains(
                "ServerCoordinationComposition.CreateAsync",
                StringComparison.Ordinal),
            "coordination pins learned pet-skill content before workers join");
        Check.True(
            !program.Contains(
                "EnsureSeedDataAsync",
                StringComparison.Ordinal),
            "server startup no longer invokes the broad legacy seed boundary");
        Check.True(
            gateway.Contains(
                "PostgresSchemaStartup.InitializeAsync",
                StringComparison.Ordinal),
            "semantic gateway uses the shared schema-startup boundary");
        Check.True(
            projection.Contains(
                "FROM character_items ci",
                StringComparison.Ordinal) &&
            !projection.Contains(
                "character_item_loadout",
                StringComparison.Ordinal) &&
            !projection.Contains(
                "character_item_compact_entries",
                StringComparison.Ordinal),
            "native item projection reads authoritative rows directly");
        Check.True(
            !compose.Contains(
                "/docker-entrypoint-initdb.d",
                StringComparison.OrdinalIgnoreCase),
            "Compose cannot replay the historical SQL directory");
        Check.True(
            !secureSmokeFixture.Contains(
                "PostgresGameStore",
                StringComparison.Ordinal) &&
            secureSmokeFixture.Contains(
                "PostgresAccountStore",
                StringComparison.Ordinal) &&
            secureSmokeFixture.Contains(
                "ICharacterLifecycleCommandExecutor",
                StringComparison.Ordinal),
            "secure smoke uses focused authoritative adapters");
        return Task.CompletedTask;
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(
            Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(current.FullName, "GodswarServer.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for B20F checks.");
    }
}
