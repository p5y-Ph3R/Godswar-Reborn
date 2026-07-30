using System.Text.RegularExpressions;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Repository-text ratchet for B13's privacy, management-plane deployment,
/// and operator-artifact boundaries.
/// </summary>
internal static class B13OperationalArchitectureChecks
{
    private const string DashboardPath =
        "docs/operations/b13-dashboard-queries.md";
    private const string EvidencePath =
        "docs/data-architecture-b13-observability-readiness-20260731.md";
    private const string HealthProbePath =
        "/app/secure-healthcheck.sh";
    private const string RunbookPath =
        "docs/operations/b13-incident-runbooks.md";

    public static Task RunAsync()
    {
        var repositoryRoot = FindRepositoryRoot();

        CheckPayloadDiagnosticsBoundary(repositoryRoot);
        CheckLoopbackManagementBoundary(repositoryRoot);
        CheckComposeManagementBoundary(repositoryRoot);
        CheckManagementHealthProbe(repositoryRoot);
        CheckBoundedCriticalShutdown(repositoryRoot);
        CheckStructuredObserverComposition(repositoryRoot);
        CheckAlertArtifact(repositoryRoot);
        CheckOperationalDocumentation(repositoryRoot);

        return Task.CompletedTask;
    }

    private static void CheckStructuredObserverComposition(
        string repositoryRoot)
    {
        var program = ReadRepositoryFile(
            repositoryRoot,
            "src/Godswar.Server/Program.cs");
        foreach (var observer in new[]
                 {
                     "observability.RecordCriticalTask",
                     "observability.RecordManagement",
                     "observability.RecordOperationalState"
                 })
        {
            Check.True(
                program.Contains(observer, StringComparison.Ordinal),
                $"{observer} is composed into the runtime");
        }
    }

    private static void CheckPayloadDiagnosticsBoundary(
        string repositoryRoot)
    {
        var clientSession = ReadRepositoryFile(
            repositoryRoot,
            "src/Godswar.Server/Networking/ClientSession.cs");
        Check.True(
            Regex.IsMatch(
                clientSession,
                @"internal\s+bool\s+AllowsPayloadDiagnostics\s*=>\s*" +
                @"false\s*;",
                RegexOptions.CultureInvariant),
            "raw packet payload diagnostics are hard-disabled");
        Check.True(
            !Regex.IsMatch(
                clientSession,
                @"AllowsPayloadDiagnostics\s*=>\s*" +
                @"_transport\s+is\s+not",
                RegexOptions.CultureInvariant),
            "transport kind cannot reactivate payload diagnostics");
        Check.True(
            Regex.IsMatch(
                clientSession,
                @"if\s*\(\s*AllowsPayloadDiagnostics\s*&&",
                RegexOptions.CultureInvariant),
            "legacy send diagnostics retain the hard-disable guard");
    }

    private static void CheckLoopbackManagementBoundary(
        string repositoryRoot)
    {
        var options = ReadRepositoryFile(
            repositoryRoot,
            "src/Godswar.Server/Operations/ManagementOptions.cs");
        Check.True(
            options.Contains(
                "DefaultBindHost = \"127.0.0.1\"",
                StringComparison.Ordinal) &&
            options.Contains(
                "IPAddress.Loopback",
                StringComparison.Ordinal) &&
            options.Contains(
                "IPAddress.IPv6Loopback",
                StringComparison.Ordinal) &&
            options.Contains(
                "TryParseExactLoopback",
                StringComparison.Ordinal),
            "management options accept only exact IPv4/IPv6 loopback");
        Check.True(
            !options.Contains("IPAddress.Any", StringComparison.Ordinal) &&
            !options.Contains("IPAddress.IPv6Any", StringComparison.Ordinal),
            "management options contain no wildcard bind path");

        var server = ReadRepositoryFile(
            repositoryRoot,
            "src/Godswar.Server/Operations/ManagementHttpServer.cs");
        Check.True(
            server.Contains(
                "IPAddress.IsLoopback(remote.Address)",
                StringComparison.Ordinal),
            "management requests enforce a loopback return path");
    }

    private static void CheckComposeManagementBoundary(
        string repositoryRoot)
    {
        foreach (var relativePath in new[]
                 {
                     "docker-compose.yml",
                     "docker-compose.secure.yml"
                 })
        {
            var compose = ReadRepositoryFile(
                repositoryRoot,
                relativePath);
            var server = ExtractTopLevelService(
                compose,
                "server",
                relativePath);
            var ports = ExtractPropertyBlock(
                server,
                "ports",
                relativePath);
            Check.True(
                !ports.Contains("9090", StringComparison.Ordinal) &&
                !ports.Contains(
                    "management",
                    StringComparison.OrdinalIgnoreCase),
                $"{relativePath} does not publish management port 9090");
            Check.True(
                server.Contains("healthcheck:", StringComparison.Ordinal) &&
                server.Contains(HealthProbePath, StringComparison.Ordinal),
                $"{relativePath} uses the management health probe");
            Check.True(
                server.Contains(
                    "stop_grace_period: 45s",
                    StringComparison.Ordinal),
                $"{relativePath} reserves the bounded shutdown budget");
        }
    }

    private static void CheckBoundedCriticalShutdown(string repositoryRoot)
    {
        var program = ReadRepositoryFile(
            repositoryRoot,
            "src/Godswar.Server/Program.cs");
        var shutdown = ReadRepositoryFile(
            repositoryRoot,
            "src/Godswar.Server/Operations/CriticalTaskShutdown.cs");
        Check.True(
            program.Contains(
                "CriticalTaskKind.CheckpointWorker",
                StringComparison.Ordinal) &&
            program.Contains(
                "CriticalTaskShutdown.CompleteAsync",
                StringComparison.Ordinal),
            "checkpoint persistence is supervised and uses bounded shutdown");
        Check.True(
            shutdown.Contains(
                "WaitAsync(completionTimeout)",
                StringComparison.Ordinal) &&
            shutdown.Contains(
                "checkpoints.ForceStop()",
                StringComparison.Ordinal),
            "critical shutdown has a finite deadline and force-stop fallback");
    }

    private static void CheckManagementHealthProbe(
        string repositoryRoot)
    {
        var script = ReadRepositoryFile(
            repositoryRoot,
            "tools/docker/secure-healthcheck.sh");
        var normalized = Regex.Replace(
            script,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant);
        Check.True(
            normalized.Contains(
                "dotnet /app/Godswar.Server.dll --management-probe ready",
                StringComparison.Ordinal) &&
            normalized.Contains(
                "${GODSWAR_MANAGEMENT_PORT:-9090}",
                StringComparison.Ordinal),
            "Docker health executes the bounded management readiness probe");
        Check.True(
            !script.Contains("/proc/net/", StringComparison.Ordinal) &&
            !script.Contains("has_socket", StringComparison.Ordinal),
            "Docker health no longer treats socket presence as readiness");
    }

    private static void CheckAlertArtifact(
        string repositoryRoot)
    {
        var relativePath =
            "operations/prometheus/godswar-server-alerts.yml";
        var alertPath = RepositoryPath(repositoryRoot, relativePath);
        Check.True(
            File.Exists(alertPath),
            "the B13 Prometheus alert artifact exists");
        Check.True(
            File.Exists(RepositoryPath(repositoryRoot, RunbookPath)),
            "the B13 alert runbook exists");

        var lines = File.ReadAllLines(alertPath);
        Check.True(
            lines.Any(static line =>
                line.Trim() ==
                    "- name: godswar-server-availability") &&
            lines.Any(static line =>
                line.Trim() ==
                    "- name: godswar-server-persistence") &&
            lines.Any(static line =>
                line.Trim() ==
                    "- name: godswar-server-telemetry"),
            "alert groups cover availability, persistence, and telemetry");

        var alertStarts = lines
            .Select((line, index) => (Line: line.Trim(), Index: index))
            .Where(static value =>
                value.Line.StartsWith(
                    "- alert: ",
                    StringComparison.Ordinal))
            .ToArray();
        Check.True(
            alertStarts.Length >= 8,
            "the B13 alert artifact has an actionable finite alert set");
        var names = alertStarts
            .Select(static value =>
                value.Line["- alert: ".Length..])
            .ToArray();
        Check.Equal(
            names.Length,
            names.Distinct(StringComparer.Ordinal).Count(),
            "B13 alert names are unique");

        for (var index = 0; index < alertStarts.Length; index++)
        {
            var start = alertStarts[index].Index;
            var end = index + 1 < alertStarts.Length
                ? alertStarts[index + 1].Index
                : lines.Length;
            var block = string.Join(
                "\n",
                lines[start..end]);
            Check.True(
                block.Contains("\n        expr:", StringComparison.Ordinal) &&
                block.Contains(
                    "\n          severity:",
                    StringComparison.Ordinal) &&
                block.Contains(
                    "\n          owner:",
                    StringComparison.Ordinal) &&
                block.Contains(
                    "\n          summary:",
                    StringComparison.Ordinal) &&
                block.Contains(
                    $"\n          runbook: {RunbookPath}",
                    StringComparison.Ordinal),
                $"alert {names[index]} has expression, ownership, summary, and runbook");
        }

        Check.True(
            names.Contains(
                "GodswarServerTargetUnavailable",
                StringComparer.Ordinal) &&
            names.Contains(
                "GodswarOutboxDispatcherFaulted",
                StringComparer.Ordinal) &&
            names.Contains(
                "GodswarProgressionRetryWorkerFaulted",
                StringComparer.Ordinal) &&
            names.Contains(
                "GodswarMetricCollectorDrops",
                StringComparer.Ordinal) &&
            names.Contains(
                "GodswarCriticalTaskFaulted",
                StringComparer.Ordinal) &&
            names.Contains(
                "GodswarManagementRequestsRejected",
                StringComparer.Ordinal),
            "alerts cover target, task, management, persistence, and telemetry failures");

        var alerts = string.Join("\n", lines);
        Check.True(
            !alerts.Contains(
                "godswar_operations_",
                StringComparison.Ordinal) &&
            alerts.Contains(
                "godswar_server_operations_critical_tasks",
                StringComparison.Ordinal) &&
            alerts.Contains(
                "godswar_server_operations_management_requests",
                StringComparison.Ordinal),
            "alerts use implemented operational exporter names");
    }

    private static void CheckOperationalDocumentation(
        string repositoryRoot)
    {
        var dashboard = ReadRepositoryFile(
            repositoryRoot,
            DashboardPath);
        Check.True(
            dashboard.Contains(
                "histogram_quantile(",
                StringComparison.Ordinal) &&
            dashboard.Contains(
                "godswar_server_simulation_tick_duration_bucket",
                StringComparison.Ordinal) &&
            dashboard.Contains(
                "godswar_command_inbox_transaction_duration_ms_bucket",
                StringComparison.Ordinal),
            "dashboard uses implemented cumulative histogram buckets");
        Check.True(
            dashboard.Contains(
                "godswar_server_operations_readiness",
                StringComparison.Ordinal) &&
            dashboard.Contains(
                "godswar_server_operations_critical_tasks",
                StringComparison.Ordinal) &&
            dashboard.Contains(
                "godswar_server_operations_management_requests",
                StringComparison.Ordinal),
            "dashboard uses implemented operational metric families");

        var evidence = ReadRepositoryFile(
            repositoryRoot,
            EvidencePath);
        Check.True(
            evidence.Contains(
                "Each histogram exports `_bucket`, `_sum`, and `_count`",
                StringComparison.Ordinal) &&
            evidence.Contains(
                "godswar_server_operations_critical_tasks{task,state}",
                StringComparison.Ordinal) &&
            evidence.Contains(
                "B13 focused checks:                    PASS, 8 passed / 0 failed",
                StringComparison.Ordinal),
            "B13 evidence records final histogram, task, and focused-check contracts");
        Check.True(
            !evidence.Contains(
                "but no buckets",
                StringComparison.OrdinalIgnoreCase) &&
            !evidence.Contains(
                "no separate per-task exported gauge",
                StringComparison.OrdinalIgnoreCase),
            "B13 evidence contains no obsolete metric omissions");
    }

    private static string ExtractTopLevelService(
        string yaml,
        string serviceName,
        string sourceName)
    {
        var lines = NormalizeLines(yaml);
        var marker = $"  {serviceName}:";
        var start = Array.FindIndex(
            lines,
            line => line == marker);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"{sourceName} has no {serviceName} service.");
        }

        var end = start + 1;
        while (end < lines.Length)
        {
            var line = lines[end];
            if (!string.IsNullOrWhiteSpace(line) &&
                LeadingWhitespace(line) <= 2)
            {
                break;
            }
            end++;
        }

        return string.Join("\n", lines[(start + 1)..end]);
    }

    private static string ExtractPropertyBlock(
        string parent,
        string propertyName,
        string sourceName)
    {
        var lines = NormalizeLines(parent);
        var prefix = $"    {propertyName}:";
        var start = Array.FindIndex(
            lines,
            line => line.StartsWith(prefix, StringComparison.Ordinal));
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"{sourceName} server has no {propertyName} property.");
        }

        var end = start + 1;
        while (end < lines.Length)
        {
            var line = lines[end];
            if (line.Length > 0 &&
                LeadingWhitespace(line) <= 4)
            {
                break;
            }
            end++;
        }

        return string.Join("\n", lines[(start + 1)..end]);
    }

    private static int LeadingWhitespace(string value)
    {
        var count = 0;
        while (count < value.Length &&
               char.IsWhiteSpace(value[count]))
        {
            count++;
        }
        return count;
    }

    private static string[] NormalizeLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

    private static string ReadRepositoryFile(
        string repositoryRoot,
        string relativePath)
    {
        var path = RepositoryPath(repositoryRoot, relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required repository file was not found: {relativePath}",
                path);
        }
        return File.ReadAllText(path);
    }

    private static string RepositoryPath(
        string repositoryRoot,
        string relativePath) =>
        Path.GetFullPath(
            Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

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
}
