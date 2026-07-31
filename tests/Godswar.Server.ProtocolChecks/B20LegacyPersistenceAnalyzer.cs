using System.Text.RegularExpressions;

namespace Godswar.Server.ProtocolChecks;

internal readonly record struct B20LegacyDependencyKey(
    B20LegacyDependencyKind Kind,
    string Path);

internal sealed record B20LegacyPersistenceAnalysis(
    int BaselineReferenceCount,
    int CurrentReferenceCount,
    int BaselineConfigurationCount,
    int CurrentConfigurationCount,
    IReadOnlyDictionary<B20LegacyDependencyKind, int> CategoryCounts,
    IReadOnlyList<string> NewDebt,
    IReadOnlyList<string> StaleDebt,
    IReadOnlyList<string> RuleViolations)
{
    public bool IsClean =>
        NewDebt.Count == 0 &&
        StaleDebt.Count == 0 &&
        RuleViolations.Count == 0;
}

internal static class B20LegacyPersistenceAnalyzer
{
    private static readonly Regex BroadStorePattern = Token("IGameStore");
    private static readonly Regex PostgresBroadStorePattern =
        Token("PostgresGameStore");
    private static readonly Regex JsonStorePattern = Token("JsonGameStore");
    private static readonly Regex JsonProviderPattern = new(
        @"\bGameStorageProviderKind\s*\.\s*Json\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex JsonSnapshotPattern = new(
        @"\bCharacterSnapshotProvider\s*\.\s*Json\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex JsonWorldContentPattern =
        Token("GeneratedWorldContentReaderLoader");
    private static readonly Regex LegacyCheckpointPattern =
        Token("LegacyCharacterCheckpointStore");
    private static readonly Regex LegacyGatewayPattern =
        Token("LegacySemanticGatewayDataSession");
    private static readonly Regex JsonStatePattern = Token("GameDatabase");
    private static readonly Regex JsonCheckpointCallPattern = new(
        @"\bjsonStore\s*(?:[!?]\s*)?\.\s*" +
        @"SaveCharacterPositionCheckpointAsync\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex JsonToolConfigurationPattern = new(
        @"(?:\bGODSWAR_STORAGE_PROVIDER\b[^\r\n]{0,96}?(?:=|:)\s*" +
        @"[""']?Json\b[""']?|\bprovider\s*=\s*[""']Json\b[""'])",
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex LegacyBootstrapPattern =
        Token("LegacySchemaBootstrap");
    private static readonly Regex LegacyLoadoutPattern = new(
        @"\b(?:FROM|JOIN)\s+(?:public\.)?" +
        @"character_item_loadout\b",
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex CaptureBackedContentPattern = new(
        @"\b(?:FROM|JOIN)\s+(?:public\.)?" +
        @"(?:monster_spawn_packets|server_packet_templates)\b",
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex GeneratedSeedPattern = new(
        @"\b(?:MapTemplateSeeds|NpcTemplateSeeds|MonsterTemplateSeeds|" +
        @"ItemTemplateSeeds|ItemAttributeTemplateSeeds|SkillTalentSeeds)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex LegacyDockerInitMountPattern = new(
        @"(?:\./)?database/postgres\s*:\s*" +
        @"/docker-entrypoint-initdb\.d",
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex CaptureAuthorityTablePattern = new(
        @"\b(?:packet_capture_sessions|packet_transactions)\b",
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex CaptureCorpusPathPattern = new(
        @"[""'][^""'\r\n]*(?:captures|_reference|origin_disasm)" +
        @"[^""'\r\n]*[""']",
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);

    public static B20LegacyPersistenceAnalysis Analyze(
        IReadOnlyDictionary<string, string> repositoryFiles,
        IReadOnlyCollection<string> jsonProviderConfigurations,
        B20LegacyPersistenceBaselineSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(repositoryFiles);
        ArgumentNullException.ThrowIfNull(jsonProviderConfigurations);
        ArgumentNullException.ThrowIfNull(baseline);

        var actual = CountReferences(repositoryFiles);
        var expected = BuildExpected(baseline.References);
        var newDebt = new List<string>();
        var staleDebt = new List<string>();
        CompareReferences(expected, actual, newDebt, staleDebt);
        CompareConfigurations(
            baseline.JsonProviderConfigurations,
            jsonProviderConfigurations,
            newDebt,
            staleDebt);

        var ruleViolations = FindCaptureAuthorityViolations(
            repositoryFiles);
        if (baseline.RetirementComplete &&
            (baseline.References.Count != 0 ||
             baseline.JsonProviderConfigurations.Count != 0))
        {
            ruleViolations.Add(
                "RetirementComplete requires an empty legacy baseline.");
        }

        var categoryCounts = actual
            .GroupBy(static pair => pair.Key.Kind)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(static pair => pair.Value));
        return new B20LegacyPersistenceAnalysis(
            expected.Values.Sum(),
            actual.Values.Sum(),
            baseline.JsonProviderConfigurations.Count,
            jsonProviderConfigurations.Count,
            categoryCounts,
            newDebt.Order(StringComparer.Ordinal).ToArray(),
            staleDebt.Order(StringComparer.Ordinal).ToArray(),
            ruleViolations.Order(StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<B20LegacyDependencyKey, int>
        CountReferences(
            IReadOnlyDictionary<string, string> repositoryFiles)
    {
        var result = new Dictionary<B20LegacyDependencyKey, int>();
        foreach (var (path, source) in repositoryFiles)
        {
            foreach (var kind in Enum.GetValues<B20LegacyDependencyKind>())
            {
                if (kind == B20LegacyDependencyKind.GeneratedSeedConsumer &&
                    (!path.StartsWith(
                         "src/Godswar.Server/",
                         StringComparison.Ordinal) ||
                     IsGeneratedDeclaration(path)))
                {
                    continue;
                }

                var count = Pattern(kind).Matches(source).Count;
                if (count > 0)
                {
                    result.Add(new B20LegacyDependencyKey(kind, path), count);
                }
            }
        }

        return result;
    }

    private static Dictionary<B20LegacyDependencyKey, int> BuildExpected(
        IEnumerable<B20LegacyDependencyAllowance> allowances)
    {
        var result = new Dictionary<B20LegacyDependencyKey, int>();
        foreach (var allowance in allowances)
        {
            if (!Enum.IsDefined(allowance.Kind) ||
                string.IsNullOrWhiteSpace(allowance.Path) ||
                allowance.Count <= 0 ||
                !result.TryAdd(
                    new B20LegacyDependencyKey(
                        allowance.Kind,
                        allowance.Path),
                    allowance.Count))
            {
                throw new InvalidDataException(
                    "The B20 legacy baseline is malformed or duplicated.");
            }
        }

        return result;
    }

    private static void CompareReferences(
        IReadOnlyDictionary<B20LegacyDependencyKey, int> expected,
        IReadOnlyDictionary<B20LegacyDependencyKey, int> actual,
        ICollection<string> newDebt,
        ICollection<string> staleDebt)
    {
        foreach (var (key, count) in actual)
        {
            expected.TryGetValue(key, out var allowed);
            if (count > allowed)
            {
                newDebt.Add(
                    $"category={key.Kind} path={key.Path} " +
                    $"baseline={allowed} current={count}");
            }
        }

        foreach (var (key, count) in expected)
        {
            actual.TryGetValue(key, out var current);
            if (current < count)
            {
                staleDebt.Add(
                    $"category={key.Kind} path={key.Path} " +
                    $"baseline={count} current={current}; " +
                    "shrink the B20 baseline");
            }
        }
    }

    private static void CompareConfigurations(
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> actual,
        ICollection<string> newDebt,
        ICollection<string> staleDebt)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        foreach (var path in actualSet.Except(expectedSet))
        {
            newDebt.Add(
                $"category=JsonProviderConfiguration path={path} " +
                "baseline=0 current=1");
        }

        foreach (var path in expectedSet.Except(actualSet))
        {
            staleDebt.Add(
                $"category=JsonProviderConfiguration path={path} " +
                "baseline=1 current=0; shrink the B20 baseline");
        }
    }

    private static List<string> FindCaptureAuthorityViolations(
        IReadOnlyDictionary<string, string> repositoryFiles)
    {
        var violations = new List<string>();
        foreach (var (path, source) in repositoryFiles)
        {
            if (!path.StartsWith(
                    "src/Godswar.Server/",
                    StringComparison.Ordinal) ||
                path.StartsWith(
                    "src/Godswar.Server/State/DatabaseMigrations/",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (CaptureAuthorityTablePattern.IsMatch(source))
            {
                violations.Add(
                    $"runtime capture authority table access in {path}");
            }
            if (CaptureCorpusPathPattern.IsMatch(source))
            {
                violations.Add(
                    $"runtime capture corpus path access in {path}");
            }
        }

        return violations;
    }

    private static Regex Pattern(B20LegacyDependencyKind kind) =>
        kind switch
        {
            B20LegacyDependencyKind.BroadStoreType => BroadStorePattern,
            B20LegacyDependencyKind.PostgresBroadStoreType =>
                PostgresBroadStorePattern,
            B20LegacyDependencyKind.JsonStoreType => JsonStorePattern,
            B20LegacyDependencyKind.JsonProviderBranch =>
                JsonProviderPattern,
            B20LegacyDependencyKind.JsonSnapshotBranch =>
                JsonSnapshotPattern,
            B20LegacyDependencyKind.JsonWorldContentFallback =>
                JsonWorldContentPattern,
            B20LegacyDependencyKind.LegacyCheckpointAdapter =>
                LegacyCheckpointPattern,
            B20LegacyDependencyKind.LegacyGatewayAdapter =>
                LegacyGatewayPattern,
            B20LegacyDependencyKind.JsonStateAggregate => JsonStatePattern,
            B20LegacyDependencyKind.JsonCheckpointCall =>
                JsonCheckpointCallPattern,
            B20LegacyDependencyKind.JsonToolConfiguration =>
                JsonToolConfigurationPattern,
            B20LegacyDependencyKind.LegacySchemaBootstrap =>
                LegacyBootstrapPattern,
            B20LegacyDependencyKind.LegacyLoadoutProjection =>
                LegacyLoadoutPattern,
            B20LegacyDependencyKind.CaptureBackedContentTable =>
                CaptureBackedContentPattern,
            B20LegacyDependencyKind.GeneratedSeedConsumer =>
                GeneratedSeedPattern,
            B20LegacyDependencyKind.LegacyDockerInitMount =>
                LegacyDockerInitMountPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static Regex Token(string value) => new(
        $@"\b{Regex.Escape(value)}\b",
        RegexOptions.CultureInvariant);

    private static bool IsGeneratedDeclaration(string path) =>
        Path.GetFileName(path).Contains(
            ".Generated",
            StringComparison.Ordinal);
}
