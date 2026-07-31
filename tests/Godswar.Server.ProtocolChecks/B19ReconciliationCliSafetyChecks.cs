using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class B19ReconciliationCliSafetyChecks
{
    internal const string CheckName =
        "B19 reconciliation CLI safety boundary";

    private const string ConnectionVariable =
        "GODSWAR_POSTGRES_RECONCILIATION_CONNECTION_STRING";
    private const string UnrelatedConnectionVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var priorExitCode = Environment.ExitCode;
        var priorConnection =
            Environment.GetEnvironmentVariable(ConnectionVariable);
        var priorUnrelated =
            Environment.GetEnvironmentVariable(
                UnrelatedConnectionVariable);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"godswar-b19-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable(
                ConnectionVariable,
                null);
            Environment.SetEnvironmentVariable(
                UnrelatedConnectionVariable,
                "Host=127.0.0.1;Database=must_not_be_used");
            await CheckCommandSelectionAsync(directory);
            await CheckRepairParsingAsync(directory);
            await CheckNamedEnvironmentOnlyAsync(directory);
            await CheckNoOverwriteAsync(directory);
            CheckStaticEvidencePolicy();
        }
        finally
        {
            Environment.ExitCode = priorExitCode;
            Environment.SetEnvironmentVariable(
                ConnectionVariable,
                priorConnection);
            Environment.SetEnvironmentVariable(
                UnrelatedConnectionVariable,
                priorUnrelated);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CheckCommandSelectionAsync(string directory)
    {
        Check.True(
            !await PostgresReconciliationCommand.TryRunAsync(
                ["--not-reconciliation"]),
            "unrelated startup commands are not consumed");

        const string relativeName = "relative.json";
        var path = Path.Combine(directory, relativeName);
        var originalDirectory = Directory.GetCurrentDirectory();
        InvocationResult result;
        try
        {
            Directory.SetCurrentDirectory(directory);
            result = await InvokeAsync(
            [
                PostgresReconciliationCommand.Mode,
                "report",
                relativeName
            ]);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }

        Check.True(
            result.Handled &&
            result.ExitCode == 2 &&
            !File.Exists(path),
            "CLI rejects non-absolute evidence paths before I/O");
    }

    private static async Task CheckRepairParsingAsync(string directory)
    {
        var path = Path.Combine(directory, "repair.json");
        foreach (var args in new[]
                 {
                     new[]
                     {
                         PostgresReconciliationCommand.Mode,
                         "repair-expired-outbox",
                         path
                     },
                     new[]
                     {
                         PostgresReconciliationCommand.Mode,
                         "repair-expired-outbox",
                         path,
                         "--allow-repair",
                         "--max",
                         "0"
                     },
                     new[]
                     {
                         PostgresReconciliationCommand.Mode,
                         "repair-expired-outbox",
                         path,
                         "--allow-repair",
                         "--max",
                         "501"
                     },
                     new[]
                     {
                         PostgresReconciliationCommand.Mode,
                         "repair-expired-outbox",
                         path,
                         "--allow-repair",
                         "--allow-repair"
                     }
                 })
        {
            var result = await InvokeAsync(args);
            Check.True(
                result.Handled &&
                result.ExitCode == 2 &&
                !File.Exists(path) &&
                !File.Exists(path + ".receipt.json"),
                "malformed or unauthorized repair is rejected before I/O");
        }
    }

    private static async Task CheckNamedEnvironmentOnlyAsync(
        string directory)
    {
        var path = Path.Combine(directory, "named-env.json");
        var result = await InvokeAsync(
        [
            PostgresReconciliationCommand.Mode,
            "report",
            path
        ]);
        Check.True(
            result.Handled &&
            result.ExitCode == 2 &&
            result.Error.Contains(
                "connection environment variable",
                StringComparison.Ordinal) &&
            !File.Exists(path),
            "an unrelated test connection variable cannot authorize CLI access");
        Check.True(
            !result.Error.Contains(
                "must_not_be_used",
                StringComparison.Ordinal),
            "connection material is never printed");
    }

    private static async Task CheckNoOverwriteAsync(string directory)
    {
        Environment.SetEnvironmentVariable(
            ConnectionVariable,
            "Host=127.0.0.1;Database=never_opened;Password=b19-secret");
        var path = Path.Combine(directory, "existing.json");
        var receipt = path + ".receipt.json";
        const string originalReport = "original-report";
        const string originalReceipt = "original-receipt";
        File.WriteAllText(path, originalReport);
        File.WriteAllText(receipt, originalReceipt);

        var result = await InvokeAsync(
        [
            PostgresReconciliationCommand.Mode,
            "report",
            path
        ]);
        Check.True(
            result.Handled &&
            result.ExitCode == 2 &&
            File.ReadAllText(path) == originalReport &&
            File.ReadAllText(receipt) == originalReceipt,
            "existing report and receipt are never overwritten");
        Check.True(
            !result.Output.Contains("b19-secret", StringComparison.Ordinal) &&
            !result.Error.Contains("b19-secret", StringComparison.Ordinal),
            "CLI output never exposes connection secrets");

        File.Delete(path);
        var receiptOnly = await InvokeAsync(
        [
            PostgresReconciliationCommand.Mode,
            "report",
            path
        ]);
        Check.True(
            receiptOnly.ExitCode == 2 &&
            !File.Exists(path) &&
            File.ReadAllText(receipt) == originalReceipt,
            "an existing receipt also blocks report creation");
        Environment.SetEnvironmentVariable(ConnectionVariable, null);
    }

    private static void CheckStaticEvidencePolicy()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Operations",
            "PostgresReconciliationCommand.cs"));
        Check.True(
            source.Contains(
                "\"GODSWAR_POSTGRES_RECONCILIATION_CONNECTION_STRING\"",
                StringComparison.Ordinal) &&
            source.Contains(
                "GetEnvironmentVariable(\n" +
                "                ConnectionStringEnvironmentVariable)",
                StringComparison.Ordinal),
            "CLI reads only its dedicated named connection environment");
        AssertOrdered(
            source,
            "ReportReservation.TryCreate(request.OutputPath)",
            "PostgresReconciliationCommandRuntime.ExecuteAsync(");
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Reconciliation",
            "PostgresReconciliationCommandRuntime.cs"));
        Check.True(
            source.Contains(
                "PostgresReconciliationCommandRuntime.ExecuteAsync(",
                StringComparison.Ordinal) &&
            !source.Contains(
                "Npgsql",
                StringComparison.Ordinal) &&
            runtime.Contains(
                "NpgsqlDataSource.Create(options.ConnectionString)",
                StringComparison.Ordinal),
            "Operations reserves evidence before delegating PostgreSQL " +
            "composition to Infrastructure");
        AssertOrdered(
            source,
            "await WriteReportAsync(",
            "Environment.ExitCode =\n" +
            "                report.Status");
        Check.True(
            source.Contains(
                "report.Status == ReconciliationRunStatus.Completed &&",
                StringComparison.Ordinal) &&
            source.Contains(
                "report.Findings.Count == 0",
                StringComparison.Ordinal) &&
            source.Contains(
                "ReconciliationRunStatus.Truncated => \"truncated\"",
                StringComparison.Ordinal) &&
            source.Contains(
                "ReconciliationRunStatus.TimedOut => \"timed_out\"",
                StringComparison.Ordinal),
            "findings, truncation, and timeout write evidence but exit nonzero");
        Check.True(
            source.Contains(
                "internal const int MaximumReportBytes = 64 * 1024",
                StringComparison.Ordinal) &&
            source.Contains(
                "FileMode.CreateNew",
                StringComparison.Ordinal) &&
            source.Contains(
                "await reservation.WriteAsync(",
                StringComparison.Ordinal),
            "CLI evidence is bounded and exclusively reserved without overwrite");

        var reportContract = source[
            source.IndexOf(
                "private sealed record CliReport",
                StringComparison.Ordinal)..];
        Check.True(
            !reportContract.Contains(
                "AccountId",
                StringComparison.Ordinal) &&
            !reportContract.Contains(
                "CharacterId",
                StringComparison.Ordinal) &&
            !reportContract.Contains(
                "OperationId",
                StringComparison.Ordinal) &&
            !reportContract.Contains(
                "ConnectionString",
                StringComparison.Ordinal),
            "report and receipt contracts contain no identity or secret field");
    }

    private static async Task<InvocationResult> InvokeAsync(
        string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Environment.ExitCode = 0;
            var handled =
                await PostgresReconciliationCommand.TryRunAsync(args);
            return new InvocationResult(
                handled,
                Environment.ExitCode,
                output.ToString(),
                error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static void AssertOrdered(
        string source,
        string first,
        string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Check.True(
            firstIndex >= 0 && secondIndex > firstIndex,
            $"{first} precedes {second}");
    }

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

    private readonly record struct InvocationResult(
        bool Handled,
        int ExitCode,
        string Output,
        string Error);
}
