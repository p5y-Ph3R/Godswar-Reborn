using System.Text.Json;

namespace Godswar.Server.ProtocolChecks;

internal static class B20LegacyPersistenceArchitectureChecks
{
    public const string CheckName =
        "B20A legacy persistence retirement ratchet";
    private const int MaximumReportedViolations = 24;

    private static readonly HashSet<string> ReadMembers = new(
        StringComparer.Ordinal)
    {
        "FindAccountByIdAsync",
        "FindAccountByUsernameAsync",
        "FindAccountCredentialAsync",
        "GetActiveWorldBossRespawnAsync",
        "GetCharacterStatsAsync",
        "GetExperienceBoostStateAsync",
        "GetFirstCharacterAsync",
        "GetOwnedPetsAsync",
        "GetSkillStatesAsync"
    };

    public static Task RunAsync()
    {
        RunAnalyzerSelfChecks();
        var repositoryRoot = FindRepositoryRoot();
        var files = LoadRepositoryFiles(repositoryRoot);
        var configurations = ReadJsonProviderConfigurations(
            repositoryRoot);
        var analysis = B20LegacyPersistenceAnalyzer.Analyze(
            files,
            configurations,
            B20LegacyPersistenceBaseline.Snapshot);
        var boundary = AnalyzeBroadStoreCalls(files);
        var calls = ClassifyCalls();
        var jsonSpecificCalls = Count(
            analysis,
            B20LegacyDependencyKind.JsonCheckpointCall);
        var totalCalls = checked(
            boundary.CurrentCallCount + jsonSpecificCalls);
        var jsonStoreFiles = CountImplementationFiles(
            files.Keys,
            "JsonGameStore");
        var postgresStoreFiles = CountImplementationFiles(
            files.Keys,
            "PostgresGameStore");

        Console.WriteLine(
            "B20_LEGACY_PERSISTENCE_RATCHET " +
            $"calls_baseline={B20LegacyPersistenceBaseline.ExpectedBroadStoreCalls + B20LegacyPersistenceBaseline.ExpectedJsonSpecificCalls} " +
            $"calls_current={totalCalls} " +
            $"reads={calls.Reads} " +
            $"mutation_or_mixed={calls.Mutations} " +
            $"bootstrap={calls.Bootstrap} " +
            $"broad_methods={DataBoundaryArchitectureBaseline.GameStoreMethods.Length} " +
            $"json_store_files={jsonStoreFiles} " +
            $"postgres_broad_store_files={postgresStoreFiles} " +
            $"json_provider_refs={Count(analysis, B20LegacyDependencyKind.JsonProviderBranch)} " +
            $"json_configs={analysis.CurrentConfigurationCount} " +
            $"json_tool_selections={Count(analysis, B20LegacyDependencyKind.JsonToolConfiguration)} " +
            $"generated_seed_refs={Count(analysis, B20LegacyDependencyKind.GeneratedSeedConsumer)} " +
            $"capture_authority_violations={analysis.RuleViolations.Count} " +
            $"new={analysis.NewDebt.Count + boundary.NewDebt.Count} " +
            $"stale={analysis.StaleDebt.Count + boundary.StaleDebt.Count}");

        var violations = analysis.NewDebt
            .Select(static value => $"NEW {value}")
            .Concat(analysis.StaleDebt.Select(static value => $"STALE {value}"))
            .Concat(analysis.RuleViolations.Select(static value => $"RULE {value}"))
            .Concat(boundary.NewDebt.Select(static value => $"NEW {value}"))
            .Concat(boundary.StaleDebt.Select(static value => $"STALE {value}"))
            .Concat(boundary.RuleViolations.Select(static value => $"RULE {value}"))
            .ToArray();
        if (violations.Length > 0)
        {
            throw new InvalidOperationException(
                "B20 legacy-persistence usage increased or its reviewed " +
                "baseline is stale. New debt is forbidden; reduce the " +
                "baseline in the same change that removes debt.\n" +
                string.Join(
                    "\n",
                    violations
                        .Take(MaximumReportedViolations)
                        .Select(static value => $"- {value}")) +
                (violations.Length > MaximumReportedViolations
                    ? $"\n- ... {violations.Length - MaximumReportedViolations} more"
                    : string.Empty));
        }

        Check.Equal(
            B20LegacyPersistenceBaseline.ExpectedBroadStoreCalls,
            boundary.CurrentCallCount,
            "complete reviewed IGameStore invocation count");
        Check.Equal(
            B20LegacyPersistenceBaseline.ExpectedJsonSpecificCalls,
            jsonSpecificCalls,
            "reviewed concrete JSON checkpoint invocation count");
        Check.Equal(
            B20LegacyPersistenceBaseline.ExpectedReadCalls,
            calls.Reads,
            "reviewed legacy read invocation count");
        Check.Equal(
            B20LegacyPersistenceBaseline.ExpectedMutationOrMixedCalls,
            calls.Mutations,
            "reviewed legacy mutation or mixed invocation count");
        Check.Equal(
            B20LegacyPersistenceBaseline.ExpectedBootstrapCalls,
            calls.Bootstrap,
            "reviewed legacy bootstrap invocation count");
        Check.Equal(
            B20LegacyPersistenceBaseline.ExpectedJsonStoreImplementationFiles,
            jsonStoreFiles,
            "JSON store implementation file count");
        Check.Equal(
            B20LegacyPersistenceBaseline.ExpectedPostgresStoreImplementationFiles,
            postgresStoreFiles,
            "broad PostgreSQL store implementation file count");
        return Task.CompletedTask;
    }

    private static DataBoundaryAnalysis AnalyzeBroadStoreCalls(
        IReadOnlyDictionary<string, string> files)
    {
        const string prefix = "src/Godswar.Server/";
        var serverFiles = files
            .Where(pair =>
                pair.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                pair.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(
                pair => pair.Key[prefix.Length..],
                static pair => pair.Value,
                StringComparer.Ordinal);
        return DataBoundaryArchitectureAnalyzer.Analyze(
            serverFiles,
            DataBoundaryArchitectureBaseline.Snapshot);
    }

    private static (int Reads, int Mutations, int Bootstrap)
        ClassifyCalls()
    {
        var reads = 0;
        var mutations =
            B20LegacyPersistenceBaseline.ExpectedJsonSpecificCalls;
        var bootstrap = 0;
        foreach (var allowance in DataBoundaryArchitectureBaseline.StoreCalls)
        {
            if (allowance.Member == "EnsureSeedDataAsync")
            {
                bootstrap += allowance.Count;
            }
            else if (ReadMembers.Contains(allowance.Member))
            {
                reads += allowance.Count;
            }
            else
            {
                mutations += allowance.Count;
            }
        }

        return (reads, mutations, bootstrap);
    }

    private static int Count(
        B20LegacyPersistenceAnalysis analysis,
        B20LegacyDependencyKind kind) =>
        analysis.CategoryCounts.TryGetValue(kind, out var count)
            ? count
            : 0;

    private static int CountImplementationFiles(
        IEnumerable<string> paths,
        string prefix) =>
        paths.Count(path =>
            path.StartsWith(
                "src/Godswar.Server/State/",
                StringComparison.Ordinal) &&
            Path.GetFileName(path).StartsWith(
                prefix,
                StringComparison.Ordinal) &&
            path.EndsWith(".cs", StringComparison.Ordinal));

    private static IReadOnlyDictionary<string, string> LoadRepositoryFiles(
        string repositoryRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        AddFiles(
            result,
            repositoryRoot,
            Path.Combine(repositoryRoot, "src", "Godswar.Server"),
            [".cs", ".csproj"]);
        AddFiles(
            result,
            repositoryRoot,
            Path.Combine(repositoryRoot, "tools"),
            [".cs", ".ps1"]);
        AddFiles(
            result,
            repositoryRoot,
            Path.Combine(repositoryRoot, ".github", "workflows"),
            [".yml", ".yaml"]);
        foreach (var path in Directory.EnumerateFiles(
                     repositoryRoot,
                     "docker-compose*.yml",
                     SearchOption.TopDirectoryOnly))
        {
            AddFile(result, repositoryRoot, path);
        }

        return result;
    }

    private static void AddFiles(
        IDictionary<string, string> destination,
        string repositoryRoot,
        string searchRoot,
        string[] extensions)
    {
        if (!Directory.Exists(searchRoot))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     searchRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (HasExcludedSegment(searchRoot, path) ||
                !extensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            AddFile(destination, repositoryRoot, path);
        }
    }

    private static void AddFile(
        IDictionary<string, string> destination,
        string repositoryRoot,
        string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path)
            .Replace('\\', '/');
        destination.Add(relative, File.ReadAllText(path));
    }

    private static bool HasExcludedSegment(
        string searchRoot,
        string path) =>
        Path.GetRelativePath(searchRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(static segment => segment.Equals(
                    "bin",
                    StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyCollection<string>
        ReadJsonProviderConfigurations(string repositoryRoot)
    {
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     repositoryRoot,
                     "appsettings*.json",
                     SearchOption.TopDirectoryOnly))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!TryReadStorageProvider(document.RootElement, out var provider) ||
                !string.Equals(
                    provider,
                    "Json",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(
                Path.GetRelativePath(repositoryRoot, path)
                    .Replace('\\', '/'));
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool TryReadStorageProvider(
        JsonElement root,
        out string? provider)
    {
        provider = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(
                    "storage",
                    StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var storageProperty in property.Value.EnumerateObject())
            {
                if (storageProperty.Name.Equals(
                        "provider",
                        StringComparison.OrdinalIgnoreCase) &&
                    storageProperty.Value.ValueKind == JsonValueKind.String)
                {
                    provider = storageProperty.Value.GetString();
                    return true;
                }
            }
        }

        return false;
    }

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
            B20LegacyPersistenceAnalyzer.Analyze(
                    clean,
                    [],
                    retired)
                .NewDebt.Count == 1,
            "B20 hard-zero retirement rejects reintroduced legacy usage");
    }

    private static Dictionary<string, string> Copy(
        IReadOnlyDictionary<string, string> source) =>
        source.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable(
            "GODSWAR_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) &&
            IsRepositoryRoot(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            for (var candidate = new DirectoryInfo(seed);
                 candidate is not null;
                 candidate = candidate.Parent)
            {
                if (IsRepositoryRoot(candidate.FullName))
                {
                    return candidate.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the B20 repository root.");
    }

    private static bool IsRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "AGENTS.md")) &&
        File.Exists(Path.Combine(path, "GodswarServer.sln"));
}
