using Godswar.Server.State;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.ProtocolChecks;

internal static class DataBoundaryArchitectureChecks
{
    private const int MaximumReportedViolations = 24;

    public static Task RunAsync()
    {
        RunAnalyzerSelfChecks();

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Godswar.Server");
        var sources = LoadSources(sourceRoot);
        var analysis = DataBoundaryArchitectureAnalyzer.Analyze(
            sources,
            DataBoundaryArchitectureBaseline.Snapshot);
        var methodDebt = CompareGameStoreMethods();

        Console.WriteLine(
            "DATA_BOUNDARY_RATCHET " +
            $"calls_baseline={analysis.BaselineCallCount} " +
            $"calls_current={analysis.CurrentCallCount} " +
            $"call_files={analysis.CurrentCallFileCount} " +
            $"call_members={analysis.CurrentCallMemberCount} " +
            $"store_field_refs_baseline={analysis.BaselineStoreFieldReferenceCount} " +
            $"store_field_refs_current={analysis.CurrentStoreFieldReferenceCount} " +
            $"store_parameter_refs_baseline={analysis.BaselineStoreParameterReferenceCount} " +
            $"store_parameter_refs_current={analysis.CurrentStoreParameterReferenceCount} " +
            $"type_refs_baseline={analysis.BaselineStoreTypeReferenceCount} " +
            $"type_refs_current={analysis.CurrentStoreTypeReferenceCount} " +
            $"legacy_npgsql_refs_baseline={analysis.BaselineLegacyNpgsqlReferenceCount} " +
            $"legacy_npgsql_refs_current={analysis.CurrentLegacyNpgsqlReferenceCount} " +
            $"state_game_refs_baseline={analysis.BaselineStateToGameReferenceCount} " +
            $"state_game_refs_current={analysis.CurrentStateToGameReferenceCount} " +
            $"store_methods_baseline={methodDebt.BaselineCount} " +
            $"store_methods_current={methodDebt.CurrentCount} " +
            $"new={analysis.NewDebt.Count + methodDebt.New.Count} " +
            $"stale={analysis.StaleDebt.Count + methodDebt.Stale.Count} " +
            $"rule_violations={analysis.RuleViolations.Count}");

        var violations = analysis.NewDebt
            .Select(static value => $"NEW {value}")
            .Concat(analysis.StaleDebt.Select(static value => $"STALE {value}"))
            .Concat(analysis.RuleViolations.Select(static value => $"RULE {value}"))
            .Concat(methodDebt.New.Select(static value => $"NEW {value}"))
            .Concat(methodDebt.Stale.Select(static value => $"STALE {value}"))
            .ToArray();
        if (violations.Length > 0)
        {
            throw new InvalidOperationException(
                "Data-boundary architecture ratchet failed. " +
                "New debt is forbidden; removed debt requires shrinking the " +
                "reviewed baseline.\n" +
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
            analysis.BaselineCallCount,
            analysis.CurrentCallCount,
            "reviewed legacy store-call count");
        Check.Equal(
            DataBoundaryArchitectureBaseline.StoreCalls
                .Select(static allowance => allowance.Path)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            analysis.CurrentCallFileCount,
            "reviewed legacy store-caller file count");
        Check.Equal(
            DataBoundaryArchitectureBaseline.StoreCalls
                .Select(static allowance => allowance.Member)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            analysis.CurrentCallMemberCount,
            "reviewed legacy store member count");
        Check.Equal(
            methodDebt.BaselineCount,
            methodDebt.CurrentCount,
            "reviewed broad store method count");
        return Task.CompletedTask;
    }

    private static (
        IReadOnlyList<string> New,
        IReadOnlyList<string> Stale,
        int BaselineCount,
        int CurrentCount) CompareGameStoreMethods()
    {
        var expected = DataBoundaryArchitectureBaseline.GameStoreMethods
            .GroupBy(static method => method, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        var currentMethods = typeof(IGameStore)
            .GetMethods()
            .Where(static method => method.DeclaringType == typeof(IGameStore))
            .ToArray();
        var current = currentMethods
            .Select(static method => method.Name)
            .GroupBy(static method => method, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var (method, count) in current)
        {
            expected.TryGetValue(method, out var baseline);
            if (count > baseline)
            {
                added.Add(
                    $"IGameStore method {method}: " +
                    $"baseline={baseline}, current={count}");
            }
        }

        foreach (var (method, count) in expected)
        {
            current.TryGetValue(method, out var actual);
            if (actual < count)
            {
                removed.Add(
                    $"IGameStore method {method}: " +
                    $"baseline={count}, current={actual}; shrink the baseline");
            }
        }

        var signatureHash = BuildGameStoreSignatureFingerprint(currentMethods);
        if (!signatureHash.Equals(
                DataBoundaryArchitectureBaseline.GameStoreSignatureSha256,
                StringComparison.Ordinal))
        {
            added.Add(
                "IGameStore signature fingerprint: " +
                $"baseline={DataBoundaryArchitectureBaseline.GameStoreSignatureSha256}, " +
                $"current={signatureHash}");
        }

        return (
            added.Order(StringComparer.Ordinal).ToArray(),
            removed.Order(StringComparer.Ordinal).ToArray(),
            expected.Values.Sum(),
            current.Values.Sum());
    }

    private static string BuildGameStoreSignatureFingerprint(
        IEnumerable<MethodInfo> methods)
    {
        var canonical = methods
            .Select(method =>
                $"{GetCanonicalTypeName(method.ReturnType)} {method.Name}(" +
                string.Join(
                    ",",
                    method
                        .GetParameters()
                        .Select(static parameter =>
                            GetCanonicalTypeName(parameter.ParameterType))) +
                ")")
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n", canonical))));
    }

    private static string GetCanonicalTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return $"{GetCanonicalTypeName(type.GetElementType()!)}&";
        }

        if (type.IsArray)
        {
            return $"{GetCanonicalTypeName(type.GetElementType()!)}[]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var definition = type.GetGenericTypeDefinition();
        var name = definition.FullName ?? definition.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name[..tick];
        }

        return $"{name}<" +
               string.Join(
                   ",",
                   type
                       .GetGenericArguments()
                       .Select(GetCanonicalTypeName)) +
               ">";
    }

    private static IReadOnlyDictionary<string, string> LoadSources(
        string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Server source root was not found: {sourceRoot}");
        }

        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasBuildDirectory(sourceRoot, path))
            .ToDictionary(
                path => NormalizeRelativePath(sourceRoot, path),
                File.ReadAllText,
                StringComparer.Ordinal);
    }

    private static bool HasBuildDirectory(
        string sourceRoot,
        string path)
    {
        var relative = NormalizeRelativePath(sourceRoot, path);
        return relative
            .Split('/')
            .Any(static segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelativePath(
        string sourceRoot,
        string path) =>
        Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');

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
            "Could not locate the repository root containing " +
            "AGENTS.md and GodswarServer.sln.");
    }

    private static bool IsRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "AGENTS.md")) &&
        File.Exists(Path.Combine(path, "GodswarServer.sln"));

    private static void RunAnalyzerSelfChecks()
    {
        var baseline = new DataBoundaryBaselineSnapshot(
            [new("Game/Legacy.cs", "LoadAsync", 1)],
            [new("Game/Legacy.cs", 2)],
            [new("Game/Legacy.cs", 2)],
            [new("Game/Legacy.cs", 2)],
            [],
            [new("State/Legacy.cs", 1)],
            ["LoadAsync"]);
        var clean = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Game/Legacy.cs"] =
                "internal sealed class Legacy(IGameStore store) " +
                "{ private readonly IGameStore _store = store; " +
                "void Load() => _store.LoadAsync(); }",
            ["State/Legacy.cs"] =
                "using Godswar.Server.Game; namespace Godswar.Server.State;"
        };
        Check.True(
            DataBoundaryArchitectureAnalyzer.Analyze(clean, baseline).IsClean,
            "architecture analyzer accepts the exact reviewed baseline");

        var increased = Copy(clean);
        increased["Game/Legacy.cs"] +=
            " internal void Again() " +
            "{ var persistence = _store; persistence.LoadAsync(); }";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(increased, baseline)
                .NewDebt
                .Any(static value => value.Contains(
                    "_store identifier",
                    StringComparison.Ordinal)),
            "architecture analyzer rejects aliased broad-store calls");

        var directParameterCall = Copy(clean);
        directParameterCall["Game/Legacy.cs"] +=
            " internal void Direct() => store.LoadAsync();";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(directParameterCall, baseline)
                .NewDebt
                .Any(static value => value.Contains(
                    "store parameter",
                    StringComparison.Ordinal)),
            "architecture analyzer rejects calls on legacy store parameters");

        var newConsumer = Copy(clean);
        newConsumer["Game/NewConsumer.cs"] =
            "internal sealed class NewConsumer(IGameStore store);";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(newConsumer, baseline)
                .NewDebt
                .Any(static value => value.Contains(
                    "IGameStore",
                    StringComparison.Ordinal)),
            "architecture analyzer rejects new broad-store consumers");

        var removed = Copy(clean);
        removed["Game/Legacy.cs"] =
            "internal sealed class Legacy(IGameStore store) " +
            "{ private readonly IGameStore _store = store; }";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(removed, baseline)
                .StaleDebt
                .Any(static value => value.Contains(
                    "shrink the baseline",
                    StringComparison.Ordinal)),
            "architecture analyzer requires baseline shrink after removal");

        var leakedDriver = Copy(clean);
        leakedDriver["Networking/Bad.cs"] =
            "using Npgsql; namespace Godswar.Server.Networking;";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(leakedDriver, baseline)
                .RuleViolations
                .Any(static value => value.Contains(
                    "Npgsql",
                    StringComparison.Ordinal)),
            "architecture analyzer rejects database drivers in networking");

        var providerWordInComment = Copy(clean);
        providerWordInComment["State/Comment.cs"] =
            "/// This policy is independent of Npgsql.";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(providerWordInComment, baseline)
                .IsClean,
            "architecture analyzer does not treat a provider word in a comment as code");

        var reversedLayer = Copy(clean);
        reversedLayer["Application/Bad.cs"] =
            "using Godswar.Server.Infrastructure; " +
            "namespace Godswar.Server.Application.Bad;";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(reversedLayer, baseline)
                .RuleViolations
                .Any(static value => value.Contains(
                    "Application cannot reference",
                    StringComparison.Ordinal)),
            "architecture analyzer rejects Application-to-Infrastructure coupling");

        var gameplayInfrastructure = Copy(clean);
        gameplayInfrastructure["Game/InfrastructureLeak.cs"] =
            "using Godswar.Server.Infrastructure; " +
            "namespace Godswar.Server.Game;";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(gameplayInfrastructure, baseline)
                .RuleViolations
                .Any(static value => value.Contains(
                    "Game/Security cannot reference",
                    StringComparison.Ordinal)),
            "architecture analyzer routes gameplay through Application");

        var misplacedApplication = Copy(clean);
        misplacedApplication["Game/Misplaced.cs"] =
            "namespace Godswar.Server.Application.Misplaced;";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(misplacedApplication, baseline)
                .RuleViolations
                .Any(static value => value.Contains(
                    "declares Application outside",
                    StringComparison.Ordinal)),
            "architecture analyzer enforces namespace-to-folder placement");

        var concreteStore = Copy(clean);
        concreteStore["Game/Bad.cs"] =
            "internal sealed class Bad { object Build() => " +
            "new PostgresGameStore(\"hidden\"); }";
        Check.True(
            DataBoundaryArchitectureAnalyzer
                .Analyze(concreteStore, baseline)
                .RuleViolations
                .Any(static value => value.Contains(
                    "concrete legacy store",
                    StringComparison.Ordinal)),
            "architecture analyzer confines concrete stores to composition");
    }

    private static Dictionary<string, string> Copy(
        IReadOnlyDictionary<string, string> source) =>
        source.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
}
