using System.Security.Cryptography;
using System.Text.Json;
using Godswar.Server.Operations;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresB19ReconciliationIntegrationChecks
{
    private static readonly string[] CliEnvironmentVariables =
    [
        PostgresReconciliationCommand
            .ConnectionStringEnvironmentVariable,
        "GODSWAR_RECONCILIATION_BATCH_SIZE",
        "GODSWAR_RECONCILIATION_MAXIMUM_CHARACTERS_PER_RUN",
        "GODSWAR_RECONCILIATION_MAXIMUM_OUTBOX_EVENTS_PER_RUN",
        "GODSWAR_RECONCILIATION_COMMAND_TIMEOUT_MILLISECONDS",
        "GODSWAR_RECONCILIATION_RUN_TIMEOUT_MILLISECONDS"
    ];

    private static async Task AssertCliEvidenceAsync(
        string connectionString,
        EconomyFixture fixture)
    {
        var originalExitCode = Environment.ExitCode;
        var environment = CliEnvironmentVariables.ToDictionary(
            static name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"godswar-b19-cli-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in CliEnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            Environment.SetEnvironmentVariable(
                PostgresReconciliationCommand
                    .ConnectionStringEnvironmentVariable,
                connectionString);

            var findingPath = Path.Combine(
                directory,
                "finding-report.json");
            var finding = await InvokeCliAsync(
            [
                PostgresReconciliationCommand.Mode,
                "report",
                findingPath
            ]);
            Check.True(
                finding.Handled &&
                finding.ExitCode == 1 &&
                File.Exists(findingPath) &&
                File.Exists(findingPath + ".receipt.json"),
                "finding report writes evidence and exits nonzero");
            using (var document = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(findingPath)))
            {
                var root = document.RootElement;
                Check.Equal(
                    "completed",
                    root.GetProperty("status").GetString()!,
                    "finding report completed its bounded scan");
                Check.True(
                    root.GetProperty("findings").GetArrayLength() > 0,
                    "finding report contains finite mismatch counts");
            }
            await AssertSafeReportAsync(
                findingPath,
                connectionString,
                fixture);

            Environment.SetEnvironmentVariable(
                "GODSWAR_RECONCILIATION_BATCH_SIZE",
                "1");
            Environment.SetEnvironmentVariable(
                "GODSWAR_RECONCILIATION_MAXIMUM_CHARACTERS_PER_RUN",
                "1");
            var truncatedPath = Path.Combine(
                directory,
                "truncated-report.json");
            var truncated = await InvokeCliAsync(
            [
                PostgresReconciliationCommand.Mode,
                "report",
                truncatedPath
            ]);
            Check.True(
                truncated.Handled &&
                truncated.ExitCode == 1 &&
                File.Exists(truncatedPath) &&
                File.Exists(truncatedPath + ".receipt.json"),
                "truncated report writes evidence and exits nonzero");
            using var truncatedDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(truncatedPath));
            Check.Equal(
                "truncated",
                truncatedDocument.RootElement
                    .GetProperty("status").GetString()!,
                "truncation is explicit in CLI evidence");
            Check.True(
                truncatedDocument.RootElement
                    .GetProperty("truncated").GetBoolean(),
                "truncated evidence carries its explicit boolean");
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            foreach (var pair in environment)
            {
                Environment.SetEnvironmentVariable(
                    pair.Key,
                    pair.Value);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertSafeReportAsync(
        string reportPath,
        string connectionString,
        EconomyFixture fixture)
    {
        var reportBytes = await File.ReadAllBytesAsync(reportPath);
        var reportText = await File.ReadAllTextAsync(reportPath);
        var receiptPath = reportPath + ".receipt.json";
        using var receipt = JsonDocument.Parse(
            await File.ReadAllBytesAsync(receiptPath));
        Check.Equal(
            Convert.ToHexString(SHA256.HashData(reportBytes)),
            receipt.RootElement.GetProperty("sha256").GetString()!,
            "receipt integrity-checks the exact report bytes");
        Check.Equal(
            Path.GetFileName(reportPath),
            receipt.RootElement.GetProperty("reportFile").GetString()!,
            "receipt contains only the report file name");

        var builder =
            new NpgsqlConnectionStringBuilder(connectionString);
        foreach (var forbidden in new[]
                 {
                     FixtureUsername,
                     FixtureCharacterName,
                     TruncationSentinelUsername,
                     TruncationSentinelCharacterName,
                     builder.Password
                 }.OfType<string>().Where(static value =>
                     !string.IsNullOrWhiteSpace(value)))
        {
            Check.True(
                !reportText.Contains(
                    forbidden,
                    StringComparison.Ordinal),
                "CLI evidence contains no secret or player identity");
        }
    }

    private static async Task<CliInvocation> InvokeCliAsync(string[] args)
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
            var combined = output.ToString() + error;
            Check.True(
                !combined.Contains(
                    FixtureUsername,
                    StringComparison.Ordinal) &&
                !combined.Contains(
                    FixtureCharacterName,
                    StringComparison.Ordinal),
                "CLI console output contains no player identity");
            return new CliInvocation(handled, Environment.ExitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private readonly record struct CliInvocation(
        bool Handled,
        int ExitCode);
}
