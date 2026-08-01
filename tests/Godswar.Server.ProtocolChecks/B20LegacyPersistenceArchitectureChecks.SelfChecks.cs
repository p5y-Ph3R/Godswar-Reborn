namespace Godswar.Server.ProtocolChecks;

internal static partial class B20LegacyPersistenceArchitectureChecks
{
    private static void RunAnalyzerSelfChecks()
    {
        const string path = "src/Godswar.Server/Game/Legacy.cs";
        var baseline = new B20LegacyPersistenceBaselineSnapshot(
            RetirementComplete: false,
            [new(B20LegacyDependencyKind.BroadStoreType, path, 1)],
            ["appsettings.json"]);
        var clean = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [path] = "internal sealed class Legacy(IGameStore store);"
        };
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                clean,
                ["appsettings.json"],
                baseline).IsClean,
            "B20 analyzer accepts the exact reviewed baseline");

        var increased = Copy(clean);
        increased[path] += " internal interface More : IGameStore {}";
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    increased,
                    ["appsettings.json"],
                    baseline)
                .NewDebt.Count == 1,
            "B20 analyzer rejects increased usage in an allowed file");

        var moved = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Godswar.Server/Game/New.cs"] = clean[path]
        };
        var movedAnalysis = B20LegacyPersistenceAnalyzer.Analyze(
            moved,
            ["appsettings.json"],
            baseline);
        Check.True(
            movedAnalysis.NewDebt.Count == 1 &&
            movedAnalysis.StaleDebt.Count == 1,
            "B20 analyzer rejects moving debt to an unreviewed file");

        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    new Dictionary<string, string>(),
                    ["appsettings.json"],
                    baseline)
                .StaleDebt.Count == 1,
            "B20 analyzer requires baseline shrink after removal");

        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    clean,
                    ["appsettings.json", "appsettings.new.json"],
                    baseline)
                .NewDebt.Count == 1,
            "B20 analyzer rejects a new JSON-backed configuration");

        CheckJsonToolSelections();
        CheckCaptureAuthorityRules(clean, baseline);

        var migration = Copy(clean);
        migration[
                "src/Godswar.Server/State/DatabaseMigrations/Applied.cs"] =
            "const string Sql = \"UPDATE packet_transactions\";";
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    migration,
                    ["appsettings.json"],
                    baseline)
                .RuleViolations.Count == 0,
            "B20 analyzer does not rewrite immutable migration history");

        var retired = new B20LegacyPersistenceBaselineSnapshot(
            RetirementComplete: true,
            [],
            []);
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                new Dictionary<string, string>(),
                [],
                retired).IsClean,
            "B20 hard-zero retirement accepts an empty repository baseline");
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(clean, [], retired)
                .NewDebt.Count == 1,
            "B20 hard-zero retirement rejects reintroduced legacy usage");
    }

    private static void CheckJsonToolSelections()
    {
        const string toolPath = "tools/Smoke/Workspace.cs";
        var toolSelections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [toolPath] =
                "[\"GODSWAR_STORAGE_PROVIDER\"] = \"Json\"; " +
                "storage = new { provider = \"Json\" };"
        };
        var toolBaseline = new B20LegacyPersistenceBaselineSnapshot(
            RetirementComplete: false,
            [new(B20LegacyDependencyKind.JsonToolConfiguration, toolPath, 2)],
            []);
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                toolSelections,
                [],
                toolBaseline).IsClean,
            "B20 analyzer tracks environment and generated JSON tool profiles");

        const string workflowPath = ".github/workflows/legacy.yml";
        var yamlSelection = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [workflowPath] = "GODSWAR_STORAGE_PROVIDER: Json"
        };
        var yamlBaseline = new B20LegacyPersistenceBaselineSnapshot(
            RetirementComplete: false,
            [new(B20LegacyDependencyKind.JsonToolConfiguration, workflowPath, 1)],
            []);
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                yamlSelection,
                [],
                yamlBaseline).IsClean,
            "B20 analyzer tracks unquoted YAML JSON provider selection");
    }

    private static void CheckCaptureAuthorityRules(
        IReadOnlyDictionary<string, string> clean,
        B20LegacyPersistenceBaselineSnapshot baseline)
    {
        var capture = Copy(clean);
        capture["src/Godswar.Server/Game/CaptureReader.cs"] =
            "const string Sql = \"SELECT * FROM packet_transactions\";";
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    capture,
                    ["appsettings.json"],
                    baseline)
                .RuleViolations.Any(static value => value.Contains(
                    "capture authority table",
                    StringComparison.Ordinal)),
            "B20 analyzer rejects runtime capture-table authority");

        var tableNameIsNotAPath = Copy(clean);
        tableNameIsNotAPath["src/Godswar.Server/Game/NpcTable.cs"] =
            "const string Table = \"npc_spawn_references\";";
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                tableNameIsNotAPath,
                ["appsettings.json"],
                baseline).IsClean,
            "B20 corpus-path rule does not mistake a SQL identifier for a path");

        var capturePath = Copy(clean);
        capturePath["src/Godswar.Server/Game/CapturePath.cs"] =
            "const string Root = @\"C:\\research\\captures\\session\";";
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    capturePath,
                    ["appsettings.json"],
                    baseline)
                .RuleViolations.Any(static value => value.Contains(
                    "capture corpus path",
                    StringComparison.Ordinal)),
            "B20 analyzer rejects an actual runtime capture-corpus path");

        var approvedExporter = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["tools/ExportMonsterContentBaseline.ps1"] =
                "$query = 'SELECT object_id FROM " +
                "monster_spawn_packets ORDER BY object_id';"
        };
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                approvedExporter,
                [],
                new B20LegacyPersistenceBaselineSnapshot(false, [], []))
                .IsClean,
            "B20 analyzer permits only the reviewed read-only monster " +
            "baseline exporter boundary");

        var unapprovedExporter = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["tools/NewCaptureReader.ps1"] =
                "$query = 'SELECT object_id FROM " +
                "monster_spawn_packets ORDER BY object_id';"
        };
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    unapprovedExporter,
                    [],
                    new B20LegacyPersistenceBaselineSnapshot(false, [], []))
                .NewDebt.Count == 1,
            "B20 analyzer rejects a new tool-side capture-backed content " +
            "reader");

        var disguisedGeneratedConsumer = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["src/Godswar.Server/Game/Fake.Generated.cs"] =
                "var skills = SkillTalentSeeds.Skills;"
        };
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    disguisedGeneratedConsumer,
                    [],
                    new B20LegacyPersistenceBaselineSnapshot(false, [], []))
                .NewDebt.Count == 1,
            "B20 analyzer does not exempt arbitrary .Generated filenames");

        approvedExporter["tools/ExportMonsterContentBaseline.ps1"] +=
            " $other = 'SELECT * FROM monster_spawn_packets';";
        Check.True(
            B20LegacyPersistenceAnalyzer.Analyze(
                    approvedExporter,
                    [],
                    new B20LegacyPersistenceBaselineSnapshot(false, [], []))
                .NewDebt.Count == 1,
            "B20 analyzer rejects expansion of the approved exporter " +
            "boundary");
    }

    private static Dictionary<string, string> Copy(
        IReadOnlyDictionary<string, string> source) =>
        source.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
}
