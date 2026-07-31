using System.Text.RegularExpressions;

namespace Godswar.Server.ProtocolChecks;

internal static class B19ReconciliationArchitectureChecks
{
    internal const string CheckName =
        "B19 reconciliation architecture ratchet";

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        var application = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Application",
            "Reconciliation");
        var infrastructure = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Reconciliation");

        Check.True(
            Directory.Exists(application),
            "B19 application reconciliation contracts exist");
        Check.True(
            Directory.Exists(infrastructure),
            "B19 PostgreSQL reconciliation adapters exist");

        CheckApplicationBoundary(root, application);
        CheckEcsBoundary(root);
        CheckBoundedPostgresQueries(infrastructure);
        CheckReportOnlyEconomyBoundary(infrastructure);
        CheckSharedDataSource(root, infrastructure);
        CheckWorkerReadiness(root);
        CheckRecoveryGateSafety(root);
        return Task.CompletedTask;
    }

    private static void CheckApplicationBoundary(
        string root,
        string directory)
    {
        foreach (var path in SourceFiles(directory))
        {
            var source = File.ReadAllText(path);
            Check.True(
                !ContainsAny(
                    source,
                    "Npgsql",
                    "StackExchange.Redis",
                    "Infrastructure.Reconciliation"),
                $"{Relative(root, path)} is independent of database drivers");
        }
    }

    private static void CheckEcsBoundary(string root)
    {
        var directory = Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Ecs");
        foreach (var path in SourceFiles(directory))
        {
            var source = File.ReadAllText(path);
            Check.True(
                !ContainsAny(
                    source,
                    "Npgsql",
                    "Infrastructure.Reconciliation"),
                $"{Relative(root, path)} has no reconciliation storage coupling");
        }
    }

    private static void CheckBoundedPostgresQueries(string directory)
    {
        var sources = ReadCombinedSources(directory);
        Check.True(
            !Regex.IsMatch(
                sources,
                @"\bOFFSET\b",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant),
            "reconciliation never uses offset pagination");
        Check.True(
            !Regex.IsMatch(
                sources,
                @"\bLIMIT\s+ALL\b",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant),
            "reconciliation contains no explicit unbounded query form");
        Check.True(
            Regex.IsMatch(
                sources,
                @"\bLIMIT\s+@(?:batch|maximum|limit)",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant),
            "PostgreSQL reconciliation queries carry a parameterized limit");
        Check.True(
            sources.Contains(
                "ReadCharacterPageAsync",
                StringComparison.Ordinal) &&
            sources.Contains(
                "ReadOutboxPageAsync",
                StringComparison.Ordinal) &&
            sources.Contains(
                "ReadOutboxPositionPageAsync",
                StringComparison.Ordinal),
            "character, outbox event, and consumer-position scans expose " +
            "bounded page contracts");
    }

    private static void CheckReportOnlyEconomyBoundary(string directory)
    {
        var readerFiles = SourceFiles(directory)
            .Where(path => Path.GetFileName(path).Contains(
                "Reader",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Check.True(
            readerFiles.Length > 0,
            "a dedicated PostgreSQL report reader exists");
        foreach (var path in readerFiles)
        {
            var source = File.ReadAllText(path);
            Check.True(
                !Regex.IsMatch(
                    source,
                    @"\b(?:UPDATE|INSERT|DELETE|TRUNCATE)\b",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant),
                $"{Path.GetFileName(path)} is read-only");
        }

        var repairFiles = SourceFiles(directory)
            .Where(path => Path.GetFileName(path).Contains(
                "Repair",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Check.True(
            repairFiles.Length > 0,
            "the safe expired-outbox repair is isolated");
        foreach (var path in repairFiles)
        {
            var source = File.ReadAllText(path);
            Check.True(
                !ContainsAny(
                    source,
                    "character_base",
                    "character_items",
                    "character_economy_baseline",
                    "character_inventory_baseline_items",
                    "character_currency_ledger",
                    "character_inventory_ledger"),
                $"{Path.GetFileName(path)} cannot repair player economy value");
        }
    }

    private static void CheckSharedDataSource(
        string root,
        string infrastructure)
    {
        var commandRuntimePath = Path.Combine(
            infrastructure,
            "PostgresReconciliationCommandRuntime.cs");
        var adapterSources = string.Join(
            Environment.NewLine,
            SourceFiles(infrastructure)
                .Where(path => !string.Equals(
                    path,
                    commandRuntimePath,
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Check.True(
            !adapterSources.Contains(
                "NpgsqlDataSource.Create",
                StringComparison.Ordinal),
            "reconciliation adapters do not create a second connection pool");
        var commandRuntime = File.ReadAllText(commandRuntimePath);
        var operations = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Operations",
            "PostgresReconciliationCommand.cs"));
        Check.True(
            commandRuntime.Contains(
                "NpgsqlDataSource.Create(options.ConnectionString)",
                StringComparison.Ordinal) &&
            operations.Contains(
                "PostgresReconciliationCommandRuntime.ExecuteAsync(",
                StringComparison.Ordinal) &&
            !operations.Contains(
                "Npgsql",
                StringComparison.Ordinal),
            "one-shot CLI data-source ownership remains inside " +
            "Infrastructure and Operations only delegates");

        var runtimeFiles = Directory.EnumerateFiles(
                Path.Combine(root, "src", "Godswar.Server", "Infrastructure"),
                "PostgresApplicationDataRuntime*.cs",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        var runtime = string.Join(
            Environment.NewLine,
            runtimeFiles.Select(File.ReadAllText));
        Check.True(
            runtime.Contains(
                "PostgresReconciliationReader",
                StringComparison.Ordinal) &&
            runtime.Contains(
                "_dataSource",
                StringComparison.Ordinal),
            "reconciliation is composed over the shared application data source");
    }

    private static void CheckWorkerReadiness(string root)
    {
        var worker = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Reconciliation",
            "PostgresReconciliationWorker.cs"));
        Check.True(
            worker.Contains(
                "report.Status is ReconciliationRunStatus.Completed",
                StringComparison.Ordinal) &&
            worker.Contains(
                "or ReconciliationRunStatus.Truncated",
                StringComparison.Ordinal) &&
            worker.Contains(
                "_lastHeartbeatTimestamp = Stopwatch.GetTimestamp();",
                StringComparison.Ordinal) &&
            worker.Contains(
                "_firstPassCompleted = true;",
                StringComparison.Ordinal),
            "only a finite non-timeout pass establishes worker health");
        Check.True(
            !Regex.IsMatch(
                worker,
                @"catch\s*\(\s*(?:NpgsqlException|TimeoutException)" +
                @"[\s\S]{0,300}_lastHeartbeatTimestamp\s*=",
                RegexOptions.CultureInvariant),
            "database failures cannot refresh reconciliation heartbeat");

        var readiness = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Operations",
            "ServerReadinessMonitor.cs"));
        Check.True(
            readiness.Contains(
                "reconciliation is not { Enabled: true }",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "reconciliation.Value.FirstPassCompleted",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "reconciliation.Value.HeartbeatAge <=",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "reconciliation.Value.MaximumHealthyHeartbeatAge",
                StringComparison.Ordinal),
            "disabled reconciliation is neutral while enabled readiness " +
            "requires a fresh completed first pass");

        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "ServerReconciliationComposition.cs"));
        Check.True(
            composition.Contains(
                "runtime?.ReconciliationEnabled != true",
                StringComparison.Ordinal) &&
            composition.Contains(
                "CriticalTaskKind.Reconciliation",
                StringComparison.Ordinal),
            "the opt-in worker is supervised as a critical task");
    }

    private static void CheckRecoveryGateSafety(string root)
    {
        var gate = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "InvokeB19PostgresRecoveryGate.ps1"));
        var helpers = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "B19PostgresRecoveryGate.Helpers.ps1"));
        var reconciliation = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "B19PostgresRecoveryGate.Reconciliation.ps1"));
        var reservation = gate.IndexOf(
            "[IO.FileMode]::CreateNew",
            StringComparison.Ordinal);
        var firstDockerCall = gate.IndexOf(
            "Invoke-B19Docker",
            StringComparison.Ordinal);
        Check.True(
            reservation >= 0 &&
            gate.Contains(
                "[IO.FileShare]::None",
                StringComparison.Ordinal) &&
            firstDockerCall > reservation,
            "recovery evidence is exclusively reserved before Docker I/O");
        Check.True(
            gate.Contains(
                "sourceTreeDirty = $null",
                StringComparison.Ordinal) &&
            gate.Contains(
                "$report.sourceTreeDirty = $statusOutput.Count -gt 0",
                StringComparison.Ordinal) &&
            gate.Contains(
                "status --porcelain=v1",
                StringComparison.Ordinal),
            "recovery evidence records whether its source tree was dirty");
        Check.True(
            helpers.Contains(
                "^reborn-b19-[a-f0-9]{12}$",
                StringComparison.Ordinal) &&
            helpers.Contains(
                "'com.reborn.test-scope' -cne",
                StringComparison.Ordinal) &&
            helpers.Contains(
                "'b19-postgres-recovery'",
                StringComparison.Ordinal) &&
            helpers.Contains(
                "Test-B19ExactContainerExists",
                StringComparison.Ordinal),
            "cleanup requires an exact owned name, label, and absence check");
        Check.True(
            gate.Contains(
                "B19PostgresRecoveryGate.Reconciliation.ps1",
                StringComparison.Ordinal) &&
            reconciliation.Contains(
                "audit.detail_payload ->> 'characterId'",
                StringComparison.Ordinal) &&
            reconciliation.Contains(
                "event.event_type = 'character.purged'",
                StringComparison.Ordinal) &&
            reconciliation.Contains(
                "event.command_inbox_id = inbox.id",
                StringComparison.Ordinal) &&
            reconciliation.Contains(
                "walletUnexplainedMismatches",
                StringComparison.Ordinal) &&
            reconciliation.Contains(
                "inventoryUnexplainedMismatches",
                StringComparison.Ordinal) &&
            reconciliation.Contains(
                "$State.walletProvenPurgeRows -ne 1",
                StringComparison.Ordinal) &&
            reconciliation.Contains(
                "$State.inventoryProvenPurgeRows -ne 1",
                StringComparison.Ordinal),
            "recovery verification excludes only exact durable purge proof " +
            "and requires symmetric fixture evidence");
        Check.True(
            gate.Contains(
                "$report.cleanup.status = 'failed'",
                StringComparison.Ordinal) &&
            gate.Contains(
                "$report.status = 'failed'",
                StringComparison.Ordinal) &&
            !ContainsAny(
                gate + helpers + reconciliation,
                "docker system prune",
                "docker container prune"),
            "cleanup errors fail the receipt without broad Docker cleanup");
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(
            directory,
            "*.cs",
            SearchOption.AllDirectories);

    private static string ReadCombinedSources(string directory) =>
        string.Join(
            Environment.NewLine,
            SourceFiles(directory).Select(File.ReadAllText));

    private static bool ContainsAny(
        string source,
        params string[] values) =>
        values.Any(value => source.Contains(
            value,
            StringComparison.Ordinal));

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
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
                if (File.Exists(Path.Combine(
                        candidate.FullName,
                        "GodswarServer.sln")))
                {
                    return candidate.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate GodswarServer.sln.");
    }
}
