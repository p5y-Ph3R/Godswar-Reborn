using System.Text.RegularExpressions;

namespace Godswar.Server.ProtocolChecks;

internal readonly record struct LegacyStoreCallKey(
    string Path,
    string Member);

internal sealed record DataBoundaryAnalysis(
    int BaselineCallCount,
    int CurrentCallCount,
    int CurrentCallFileCount,
    int CurrentCallMemberCount,
    int BaselineStoreFieldReferenceCount,
    int CurrentStoreFieldReferenceCount,
    int BaselineStoreParameterReferenceCount,
    int CurrentStoreParameterReferenceCount,
    int BaselineStoreTypeReferenceCount,
    int CurrentStoreTypeReferenceCount,
    int BaselineLegacyNpgsqlReferenceCount,
    int CurrentLegacyNpgsqlReferenceCount,
    int BaselineStateToGameReferenceCount,
    int CurrentStateToGameReferenceCount,
    IReadOnlyList<string> NewDebt,
    IReadOnlyList<string> StaleDebt,
    IReadOnlyList<string> RuleViolations)
{
    public bool IsClean =>
        NewDebt.Count == 0 &&
        StaleDebt.Count == 0 &&
        RuleViolations.Count == 0;
}

internal static class DataBoundaryArchitectureAnalyzer
{
    private static readonly Regex StoreCallPattern = new(
        @"\b(?:_store|store)\s*(?:[!?]\s*)?\.\s*" +
        @"(?<member>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant);

    private static readonly Regex StoreTypePattern = new(
        @"\bIGameStore\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex StoreFieldPattern = new(
        @"\b_store\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex StoreParameterPattern = new(
        @"\bstore\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex StateToGamePattern = new(
        @"^\s*using\s+Godswar\.Server\.Game\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex NpgsqlPattern = new(
        @"^\s*using\s+Npgsql(?:\.[A-Za-z0-9_.]+)?\s*;" +
        @"|\bNpgsql[A-Z][A-Za-z0-9_]*\b",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex RedisPattern = new(
        @"\bStackExchange\s*\.\s*Redis\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex MongoPattern = new(
        @"\bMongoDB\s*\.\s*Driver\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex ConcreteStorePattern = new(
        @"\b(?:PostgresGameStore|JsonGameStore)\b",
        RegexOptions.CultureInvariant);

    public static DataBoundaryAnalysis Analyze(
        IReadOnlyDictionary<string, string> sourceFiles,
        DataBoundaryBaselineSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentNullException.ThrowIfNull(baseline);

        var storeCalls = CountStoreCalls(
            sourceFiles,
            baseline.GameStoreMethods);
        var storeFieldReferences = CountReferences(
            sourceFiles,
            StoreFieldPattern,
            static path => !IsUnder(path, "State/"));
        var storeParameterPaths = baseline.StoreParameterReferences
            .Select(static allowance => allowance.Path)
            .ToHashSet(StringComparer.Ordinal);
        var storeParameterReferences = CountReferences(
            sourceFiles,
            StoreParameterPattern,
            storeParameterPaths.Contains);
        var storeTypeReferences = CountReferences(
            sourceFiles,
            StoreTypePattern,
            static path => !IsUnder(path, "State/"));
        var legacyNpgsqlReferences = CountReferences(
            sourceFiles,
            NpgsqlPattern,
            static path => !IsUnder(path, "Infrastructure/"));
        var stateToGameReferences = CountReferences(
            sourceFiles,
            StateToGamePattern,
            static path => IsUnder(path, "State/"));

        var newDebt = new List<string>();
        var staleDebt = new List<string>();
        CompareStoreCalls(
            baseline.StoreCalls,
            storeCalls,
            newDebt,
            staleDebt);
        CompareReferences(
            "_store identifier",
            baseline.StoreFieldReferences,
            storeFieldReferences,
            newDebt,
            staleDebt);
        CompareReferences(
            "store parameter",
            baseline.StoreParameterReferences,
            storeParameterReferences,
            newDebt,
            staleDebt);
        CompareReferences(
            "IGameStore",
            baseline.StoreTypeReferences,
            storeTypeReferences,
            newDebt,
            staleDebt);
        CompareReferences(
            "legacy Npgsql",
            baseline.LegacyNpgsqlReferences,
            legacyNpgsqlReferences,
            newDebt,
            staleDebt);
        CompareReferences(
            "State -> Game using",
            baseline.StateToGameUsings,
            stateToGameReferences,
            newDebt,
            staleDebt);

        var ruleViolations = FindRuleViolations(sourceFiles);
        return new DataBoundaryAnalysis(
            baseline.StoreCalls.Sum(static allowance => allowance.Count),
            storeCalls.Values.Sum(),
            storeCalls.Keys
                .Select(static key => key.Path)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            storeCalls.Keys
                .Select(static key => key.Member)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            baseline.StoreFieldReferences.Sum(static allowance => allowance.Count),
            storeFieldReferences.Values.Sum(),
            baseline.StoreParameterReferences.Sum(static allowance => allowance.Count),
            storeParameterReferences.Values.Sum(),
            baseline.StoreTypeReferences.Sum(static allowance => allowance.Count),
            storeTypeReferences.Values.Sum(),
            baseline.LegacyNpgsqlReferences.Sum(static allowance => allowance.Count),
            legacyNpgsqlReferences.Values.Sum(),
            baseline.StateToGameUsings.Sum(static allowance => allowance.Count),
            stateToGameReferences.Values.Sum(),
            newDebt.Order(StringComparer.Ordinal).ToArray(),
            staleDebt.Order(StringComparer.Ordinal).ToArray(),
            ruleViolations.Order(StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<LegacyStoreCallKey, int> CountStoreCalls(
        IReadOnlyDictionary<string, string> sourceFiles,
        IReadOnlyList<string> gameStoreMethods)
    {
        var result = new Dictionary<LegacyStoreCallKey, int>();
        var members = gameStoreMethods.ToHashSet(StringComparer.Ordinal);
        foreach (var (path, source) in sourceFiles)
        {
            if (IsUnder(path, "State/") ||
                path.Equals("Program.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in StoreCallPattern.Matches(source))
            {
                var member = match.Groups["member"].Value;
                if (!members.Contains(member))
                {
                    continue;
                }

                var key = new LegacyStoreCallKey(
                    path,
                    member);
                result.TryGetValue(key, out var count);
                result[key] = count + 1;
            }
        }

        return result;
    }

    private static Dictionary<string, int> CountReferences(
        IReadOnlyDictionary<string, string> sourceFiles,
        Regex pattern,
        Func<string, bool> include)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, source) in sourceFiles)
        {
            if (!include(path))
            {
                continue;
            }

            var count = pattern.Matches(source).Count;
            if (count > 0)
            {
                result[path] = count;
            }
        }

        return result;
    }

    private static void CompareStoreCalls(
        IReadOnlyList<LegacyStoreCallAllowance> allowances,
        IReadOnlyDictionary<LegacyStoreCallKey, int> actual,
        ICollection<string> newDebt,
        ICollection<string> staleDebt)
    {
        var expected = allowances.ToDictionary(
            static allowance => new LegacyStoreCallKey(
                allowance.Path,
                allowance.Member),
            static allowance => allowance.Count);
        foreach (var (key, count) in actual)
        {
            expected.TryGetValue(key, out var allowed);
            if (count > allowed)
            {
                newDebt.Add(
                    $"store call {key.Path}|{key.Member}: " +
                    $"baseline={allowed}, current={count}");
            }
        }

        foreach (var (key, count) in expected)
        {
            actual.TryGetValue(key, out var current);
            if (current < count)
            {
                staleDebt.Add(
                    $"store call {key.Path}|{key.Member}: " +
                    $"baseline={count}, current={current}; shrink the baseline");
            }
        }
    }

    private static void CompareReferences(
        string dependency,
        IReadOnlyList<ReferenceAllowance> allowances,
        IReadOnlyDictionary<string, int> actual,
        ICollection<string> newDebt,
        ICollection<string> staleDebt)
    {
        var expected = allowances.ToDictionary(
            static allowance => allowance.Path,
            static allowance => allowance.Count,
            StringComparer.Ordinal);
        foreach (var (path, count) in actual)
        {
            expected.TryGetValue(path, out var allowed);
            if (count > allowed)
            {
                newDebt.Add(
                    $"{dependency} reference {path}: " +
                    $"baseline={allowed}, current={count}");
            }
        }

        foreach (var (path, count) in expected)
        {
            actual.TryGetValue(path, out var current);
            if (current < count)
            {
                staleDebt.Add(
                    $"{dependency} reference {path}: " +
                    $"baseline={count}, current={current}; shrink the baseline");
            }
        }
    }

    private static List<string> FindRuleViolations(
        IReadOnlyDictionary<string, string> sourceFiles)
    {
        var violations = new List<string>();
        foreach (var (path, source) in sourceFiles)
        {
            CheckProviderOwnership(path, source, violations);
            CheckLayerRules(path, source, violations);
            CheckLayerNamespace(path, source, "Application", violations);
            CheckLayerNamespace(path, source, "Domain", violations);
            CheckLayerNamespace(path, source, "Infrastructure", violations);
            CheckNamespaceLocation(path, source, violations);
        }

        return violations;
    }

    private static void CheckProviderOwnership(
        string path,
        string source,
        ICollection<string> violations)
    {
        var legacyProviderOwner =
            IsUnder(path, "State/") ||
            path.Equals(
                "Operations/ControlledHostValidationCommand.cs",
                StringComparison.Ordinal);
        var futureProviderOwner = IsUnder(path, "Infrastructure/");
        if (NpgsqlPattern.IsMatch(source) &&
            !legacyProviderOwner &&
            !futureProviderOwner)
        {
            violations.Add($"database driver Npgsql is forbidden in {path}");
        }

        if ((RedisPattern.IsMatch(source) || MongoPattern.IsMatch(source)) &&
            !futureProviderOwner)
        {
            violations.Add(
                $"Redis/MongoDB driver is forbidden outside Infrastructure in {path}");
        }

        var concreteStoreOwner =
            IsUnder(path, "State/") ||
            path.Equals("Program.cs", StringComparison.Ordinal);
        if (ConcreteStorePattern.IsMatch(source) && !concreteStoreOwner)
        {
            violations.Add(
                $"concrete legacy store is forbidden outside State/Program in {path}");
        }
    }

    private static void CheckLayerRules(
        string path,
        string source,
        ICollection<string> violations)
    {
        if (IsUnder(path, "Application/"))
        {
            FindForbidden(
                path,
                source,
                "Application",
                violations,
                "Godswar.Server.Infrastructure",
                "Godswar.Server.Game",
                "Godswar.Server.Networking",
                "Godswar.Server.Packets",
                "Godswar.Server.Protocol",
                "System.Net.Sockets");
        }

        if (IsUnder(path, "Game/") || IsUnder(path, "Security/"))
        {
            FindForbidden(
                path,
                source,
                "Game/Security",
                violations,
                "Godswar.Server.Infrastructure");
        }

        if (IsUnder(path, "Domain/"))
        {
            FindForbidden(
                path,
                source,
                "Domain",
                violations,
                "Godswar.Server.Application",
                "Godswar.Server.Infrastructure",
                "Godswar.Server.Game",
                "Godswar.Server.Networking",
                "Godswar.Server.Packets",
                "Godswar.Server.Protocol",
                "Godswar.Server.State",
                "System.Net.Sockets");
        }

        if (IsUnder(path, "Infrastructure/"))
        {
            FindForbidden(
                path,
                source,
                "Infrastructure",
                violations,
                "Godswar.Server.Ecs",
                "Godswar.Server.Game",
                "Godswar.Server.Networking",
                "Godswar.Server.Packets",
                "Godswar.Server.Protocol",
                "Godswar.Server.World",
                "System.Net.Sockets");
        }

        if (IsUnder(path, "Ecs/") || IsUnder(path, "World/"))
        {
            FindForbidden(
                path,
                source,
                "ECS/World",
                violations,
                "Godswar.Server.Application",
                "Godswar.Server.Infrastructure");
        }

        if (IsUnder(path, "Networking/") ||
            IsUnder(path, "Packets/") ||
            IsUnder(path, "Protocol/"))
        {
            FindForbidden(
                path,
                source,
                "transport/protocol",
                violations,
                "Godswar.Server.Infrastructure");
        }
    }

    private static void FindForbidden(
        string path,
        string source,
        string layer,
        ICollection<string> violations,
        params string[] forbiddenDependencies)
    {
        foreach (var dependency in forbiddenDependencies)
        {
            if (source.Contains(dependency, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{layer} cannot reference {dependency} in {path}");
            }
        }
    }

    private static void CheckLayerNamespace(
        string path,
        string source,
        string layer,
        ICollection<string> violations)
    {
        if (!IsUnder(path, $"{layer}/"))
        {
            return;
        }

        var expected = $"namespace Godswar.Server.{layer}";
        var declaration = new Regex(
            $@"^\s*{Regex.Escape(expected)}(?:\.|\s*[;{{])",
            RegexOptions.CultureInvariant | RegexOptions.Multiline);
        if (!declaration.IsMatch(source))
        {
            violations.Add(
                $"{path} must declare a Godswar.Server.{layer} namespace");
        }
    }

    private static void CheckNamespaceLocation(
        string path,
        string source,
        ICollection<string> violations)
    {
        var declaration = new Regex(
            @"^\s*namespace\s+Godswar\.Server\." +
            @"(?<layer>Application|Domain|Infrastructure)(?:\.|\s*[;{])",
            RegexOptions.CultureInvariant | RegexOptions.Multiline);
        foreach (Match match in declaration.Matches(source))
        {
            var layer = match.Groups["layer"].Value;
            if (!IsUnder(path, $"{layer}/"))
            {
                violations.Add(
                    $"{path} declares {layer} outside the {layer}/ directory");
            }
        }
    }

    private static bool IsUnder(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal);
}
