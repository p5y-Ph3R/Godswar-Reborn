using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal readonly record struct LegacyPersistenceTelemetryKey(
    string Path,
    LegacyPersistenceOperation Operation);

internal sealed record LegacyPersistenceTelemetryCoverage(
    int RequiredInvocations,
    int InstrumentedInvocations,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Orphaned,
    IReadOnlyList<string> AssociationViolations)
{
    public bool IsComplete =>
        Missing.Count == 0 &&
        Orphaned.Count == 0 &&
        AssociationViolations.Count == 0;
}

/// <summary>
/// Requires one finite usage-counter record for every reviewed legacy data
/// invocation. Its expected set is derived from the existing B20 baselines,
/// so removal of a persistence call also makes an obsolete metric record fail.
/// </summary>
internal static class B20LegacyPersistenceTelemetryArchitectureChecks
{
    public const string CheckName =
        "B20 legacy persistence telemetry coverage";

    private const string ServerPrefix = "src/Godswar.Server/";
    private const int MaximumReportedViolations = 24;

    public static Task RunAsync()
    {
        RunAnalyzerSelfChecks();
        var expected = BuildExpectedCoverage();
        var repositoryRoot = FindRepositoryRoot();
        var source = LoadServerSource(repositoryRoot);
        var analysis = Analyze(source, expected);

        Console.WriteLine(
            "B20_LEGACY_TELEMETRY_COVERAGE " +
            $"required={analysis.RequiredInvocations} " +
            $"instrumented={analysis.InstrumentedInvocations} " +
            $"operations={expected.Keys.Select(static key => key.Operation).Distinct().Count()} " +
            $"missing={analysis.Missing.Count} " +
            $"orphaned={analysis.Orphaned.Count} " +
            $"association_violations={analysis.AssociationViolations.Count}");

        if (!analysis.IsComplete)
        {
            var violations = analysis.Missing
                .Select(static value => $"MISSING {value}")
                .Concat(
                    analysis.Orphaned.Select(
                        static value => $"ORPHANED {value}"))
                .Concat(
                    analysis.AssociationViolations.Select(
                        static value => $"ASSOCIATION {value}"))
                .ToArray();
            throw new InvalidOperationException(
                "Every reviewed legacy persistence invocation must record " +
                "one finite operation metric before attempting storage. " +
                "Instrumentation must shrink with the source baseline.\n" +
                string.Join(
                    "\n",
                    violations
                        .Take(MaximumReportedViolations)
                        .Select(static value => $"- {value}")) +
                (violations.Length > MaximumReportedViolations
                    ? $"\n- ... {violations.Length - MaximumReportedViolations} more"
                    : string.Empty));
        }

        return Task.CompletedTask;
    }

    internal static LegacyPersistenceTelemetryCoverage Analyze(
        IReadOnlyDictionary<string, string> serverSource,
        IReadOnlyDictionary<LegacyPersistenceTelemetryKey, int> expected)
    {
        ArgumentNullException.ThrowIfNull(serverSource);
        ArgumentNullException.ThrowIfNull(expected);

        var sourceScan =
            B20LegacyPersistenceTelemetrySourceAnalyzer.Scan(
                serverSource,
                expected);
        var actual = sourceScan.Records;
        var missing = new List<string>();
        var orphaned = new List<string>();
        foreach (var (key, required) in expected)
        {
            actual.TryGetValue(key, out var instrumented);
            if (instrumented < required)
            {
                missing.Add(Describe(key, required, instrumented));
            }
        }
        foreach (var (key, instrumented) in actual)
        {
            expected.TryGetValue(key, out var required);
            if (instrumented > required)
            {
                orphaned.Add(Describe(key, required, instrumented));
            }
        }

        return new LegacyPersistenceTelemetryCoverage(
            expected.Values.Sum(),
            actual.Values.Sum(),
            missing.Order(StringComparer.Ordinal).ToArray(),
            orphaned.Order(StringComparer.Ordinal).ToArray(),
            sourceScan.AssociationViolations);
    }

    internal static IReadOnlyDictionary<
        LegacyPersistenceTelemetryKey,
        int> BuildExpectedCoverage()
    {
        var expected = new Dictionary<
            LegacyPersistenceTelemetryKey,
            int>();
        foreach (var allowance in
                 DataBoundaryArchitectureBaseline.StoreCalls)
        {
            Add(
                expected,
                new LegacyPersistenceTelemetryKey(
                    NormalizePath(allowance.Path),
                    OperationForMember(allowance.Member)),
                allowance.Count);
        }

        foreach (var allowance in
                 B20LegacyPersistenceBaseline.References.Where(
                     static candidate =>
                         candidate.Kind ==
                         B20LegacyDependencyKind.JsonCheckpointCall))
        {
            if (!allowance.Path.StartsWith(
                    ServerPrefix,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The concrete JSON checkpoint path is outside the " +
                    "server source root.");
            }
            Add(
                expected,
                new LegacyPersistenceTelemetryKey(
                    NormalizePath(allowance.Path[ServerPrefix.Length..]),
                    LegacyPersistenceOperation.
                        SaveCharacterPositionCheckpoint),
                allowance.Count);
        }

        var required = expected.Values.Sum();
        var baselineRequired = checked(
            DataBoundaryArchitectureBaseline.StoreCalls.Sum(
                static allowance => allowance.Count) +
            B20LegacyPersistenceBaseline.References
                .Where(static allowance =>
                    allowance.Kind ==
                    B20LegacyDependencyKind.JsonCheckpointCall)
                .Sum(static allowance => allowance.Count));
        if (required != baselineRequired)
        {
            throw new InvalidDataException(
                "Derived telemetry coverage does not equal the reviewed " +
                "legacy invocation baseline.");
        }

        return expected;
    }

    private static LegacyPersistenceOperation OperationForMember(
        string member)
    {
        const string suffix = "Async";
        if (string.IsNullOrWhiteSpace(member) ||
            !member.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Legacy member {member} has no Async suffix.");
        }

        var operationName = member[..^suffix.Length];
        if (!Enum.TryParse<LegacyPersistenceOperation>(
                operationName,
                ignoreCase: false,
                out var operation) ||
            !Enum.IsDefined(operation))
        {
            throw new InvalidDataException(
                $"Legacy member {member} has no finite metric operation.");
        }
        return operation;
    }

    private static void Add(
        IDictionary<LegacyPersistenceTelemetryKey, int> destination,
        LegacyPersistenceTelemetryKey key,
        int count)
    {
        if (string.IsNullOrWhiteSpace(key.Path) || count <= 0)
        {
            throw new InvalidDataException(
                "Legacy telemetry coverage contains an invalid allowance.");
        }

        destination.TryGetValue(key, out var current);
        destination[key] = checked(current + count);
    }

    private static string Describe(
        LegacyPersistenceTelemetryKey key,
        int required,
        int instrumented) =>
        $"path={key.Path} operation={key.Operation} " +
        $"required={required} instrumented={instrumented}";

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static IReadOnlyDictionary<string, string> LoadServerSource(
        string repositoryRoot)
    {
        var sourceRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Godswar.Server");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, path);
            if (relative
                .Split(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .Any(static segment =>
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(
                NormalizePath(relative),
                File.ReadAllText(path));
        }
        return result;
    }

    private static void RunAnalyzerSelfChecks()
    {
        var key = new LegacyPersistenceTelemetryKey(
            "Game/Legacy.cs",
            LegacyPersistenceOperation.GetFirstCharacter);
        var expected = new Dictionary<
            LegacyPersistenceTelemetryKey,
            int>
        {
            [key] = 1
        };
        const string record =
            "LegacyPersistenceMetrics.Record(" +
            "LegacyPersistenceOperation.GetFirstCharacter);";
        const string call =
            "await _store.GetFirstCharacterAsync();";
        var exact = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key.Path] = record + call
        };
        Check.True(
            Analyze(exact, expected).IsComplete,
            "telemetry analyzer accepts exact derived coverage");
        Check.True(
            Analyze(new Dictionary<string, string>(), expected)
                .Missing.Count == 1,
            "telemetry analyzer rejects a missing record");

        var duplicate = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key.Path] = record + record + call
        };
        Check.True(
            Analyze(duplicate, expected).Orphaned.Count == 1,
            "telemetry analyzer rejects excess records");

        var moved = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Game/Moved.cs"] = record + call
        };
        var movedAnalysis = Analyze(moved, expected);
        Check.True(
            movedAnalysis.Missing.Count == 1 &&
            movedAnalysis.Orphaned.Count == 1,
            "telemetry analyzer rejects moved instrumentation");

        var deadText = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key.Path] =
                "// " + record + "\n" +
                "/* " + record + " */\n" +
                "const string normal = \"" + record + "\";\n" +
                "const string raw = \"\"\"" + record + "\"\"\";\n" +
                call
        };
        var deadTextAnalysis = Analyze(deadText, expected);
        Check.True(
            deadTextAnalysis.Missing.Count == 1 &&
            deadTextAnalysis.InstrumentedInvocations == 0,
            "telemetry analyzer ignores records in comments and strings");

        var recordAfterCall = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            [key.Path] = call + record
        };
        var afterAnalysis = Analyze(recordAfterCall, expected);
        Check.True(
            afterAnalysis.Missing.Count == 0 &&
            afterAnalysis.Orphaned.Count == 0 &&
            afterAnalysis.AssociationViolations.Count == 1 &&
            !afterAnalysis.IsComplete,
            "telemetry analyzer rejects a record after its storage call");

        var derived = BuildExpectedCoverage();
        Check.Equal(
            DataBoundaryArchitectureBaseline.StoreCalls.Sum(
                static allowance => allowance.Count) +
            B20LegacyPersistenceBaseline.ExpectedJsonSpecificCalls,
            derived.Values.Sum(),
            "telemetry coverage is derived from both invocation baselines");
    }

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
