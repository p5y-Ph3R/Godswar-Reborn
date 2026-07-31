using System.Security.Cryptography;
using System.Text.Json;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Reconciliation;

namespace Godswar.Server.Operations;

internal static class PostgresReconciliationCommand
{
    internal const string Mode = "--postgres-reconciliation";
    internal const string ConnectionStringEnvironmentVariable =
        "GODSWAR_POSTGRES_RECONCILIATION_CONNECTION_STRING";
    internal const int MaximumReportBytes = 64 * 1024;

    public static async Task<bool> TryRunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], Mode, StringComparison.Ordinal))
        {
            return false;
        }

        Environment.ExitCode = 2;
        if (!TryParse(args, out var request))
        {
            Console.Error.WriteLine(
                "[reconciliation] expected " +
                "--postgres-reconciliation " +
                "report <absoluteReportPath>, or " +
                "repair-expired-outbox <absoluteReportPath> " +
                "--allow-repair [--max <1..500>]");
            return true;
        }

        var connectionString =
            Environment.GetEnvironmentVariable(
                ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "[reconciliation] connection environment variable " +
                "is not configured");
            return true;
        }
        var outputDirectory =
            Path.GetDirectoryName(request.OutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) ||
            !Directory.Exists(outputDirectory))
        {
            Console.Error.WriteLine(
                "[reconciliation] report directory does not exist");
            return true;
        }

        await using var reservation =
            ReportReservation.TryCreate(request.OutputPath);
        if (reservation is null)
        {
            Console.Error.WriteLine(
                "[reconciliation] report evidence path already exists");
            return true;
        }

        var repairAttemptStarted = false;
        try
        {
            var options = ReadOptionsFromEnvironment();
            var repairOptions = request.Repair
                ? ReadOutboxOptionsFromEnvironment(
                    requireExplicit: true)
                : null;
            var execution =
                await PostgresReconciliationCommandRuntime.ExecuteAsync(
                    new PostgresReconciliationCommandExecutionOptions(
                        connectionString,
                        options,
                        repairOptions,
                        request.MaximumRepairs),
                    request.Repair
                        ? () =>
                        {
                            repairAttemptStarted = true;
                            reservation.Preserve();
                        }
                        : null,
                    cancellationToken);
            var report = execution.Report;
            await WriteReportAsync(
                reservation,
                request.Repair
                    ? "repair_expired_outbox"
                    : "report",
                report,
                execution.Repair,
                cancellationToken);
            reservation.Preserve();
            Environment.ExitCode =
                report.Status == ReconciliationRunStatus.Completed &&
                report.Findings.Count == 0
                    ? 0
                    : 1;
            Console.WriteLine(
                "[reconciliation] bounded report and receipt written");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (repairAttemptStarted)
            {
                await reservation.TryWriteFailureEvidenceAsync(
                    "operation_cancelled",
                    CancellationToken.None);
            }
            Environment.ExitCode = 1;
        }
        catch (Exception exception)
        {
            if (repairAttemptStarted)
            {
                await reservation.TryWriteFailureEvidenceAsync(
                    exception.GetType().Name,
                    CancellationToken.None);
            }
            Console.Error.WriteLine(
                "[reconciliation] command failed: " +
                exception.GetType().Name);
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static ReconciliationOptions ReadOptionsFromEnvironment()
    {
        var options = new ReconciliationOptions();
        options.BatchSize = ReadInt(
            "GODSWAR_RECONCILIATION_BATCH_SIZE",
            options.BatchSize);
        options.MaximumCharactersPerRun = ReadInt(
            "GODSWAR_RECONCILIATION_MAXIMUM_CHARACTERS_PER_RUN",
            options.MaximumCharactersPerRun);
        options.MaximumOutboxEventsPerRun = ReadInt(
            "GODSWAR_RECONCILIATION_MAXIMUM_OUTBOX_EVENTS_PER_RUN",
            options.MaximumOutboxEventsPerRun);
        options.CommandTimeoutMilliseconds = ReadInt(
            "GODSWAR_RECONCILIATION_COMMAND_TIMEOUT_MILLISECONDS",
            options.CommandTimeoutMilliseconds);
        options.RunTimeoutMilliseconds = ReadInt(
            "GODSWAR_RECONCILIATION_RUN_TIMEOUT_MILLISECONDS",
            options.RunTimeoutMilliseconds);
        options.Validate();
        return options;
    }

    private static PostgresReconciliationRepairOptions
        ReadOutboxOptionsFromEnvironment(bool requireExplicit)
    {
        Func<string, int, int> read = requireExplicit
            ? ReadRequiredInt
            : ReadInt;
        return new PostgresReconciliationRepairOptions(
            BatchSize: read(
                "GODSWAR_OUTBOX_BATCH_SIZE",
                32),
            PollIntervalMilliseconds: read(
                "GODSWAR_OUTBOX_POLL_INTERVAL_MILLISECONDS",
                250),
            LeaseMilliseconds: read(
                "GODSWAR_OUTBOX_LEASE_MILLISECONDS",
                30_000),
            MaximumDeliveryAttempts: read(
                "GODSWAR_OUTBOX_MAXIMUM_DELIVERY_ATTEMPTS",
                8),
            BaseRetryDelayMilliseconds: read(
                "GODSWAR_OUTBOX_BASE_RETRY_DELAY_MILLISECONDS",
                500),
            MaximumRetryDelayMilliseconds: read(
                "GODSWAR_OUTBOX_MAXIMUM_RETRY_DELAY_MILLISECONDS",
                30_000),
            GapRetryDelayMilliseconds: read(
                "GODSWAR_OUTBOX_GAP_RETRY_DELAY_MILLISECONDS",
                1_000),
            CommandTimeoutMilliseconds: read(
                "GODSWAR_OUTBOX_COMMAND_TIMEOUT_MILLISECONDS",
                5_000));
    }

    private static async Task WriteReportAsync(
        ReportReservation reservation,
        string commandMode,
        ReconciliationReport report,
        ExpiredOutboxLeaseRepairResult? repair,
        CancellationToken cancellationToken)
    {
        var document = new CliReport(
            SchemaVersion: 1,
            CommandMode: commandMode,
            ReconciliationMode: "report_only",
            Status: ToStatus(report.Status),
            report.StartedAtUtc,
            report.DurationMilliseconds,
            report.CharacterRowsScanned,
            report.OutboxRowsScanned,
            report.Truncated,
            report.Findings
                .Select(finding => new CliFinding(
                    finding.Category.ToProtocolValue(),
                    finding.Count))
                .ToArray(),
            repair?.RecoveredCount,
            repair?.LimitReached);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            JsonDefaults.Indented);
        if (bytes.Length > MaximumReportBytes)
        {
            throw new InvalidDataException(
                "The bounded reconciliation report exceeded its limit.");
        }

        var checksum = Convert.ToHexString(
            SHA256.HashData(bytes));
        var receipt = new CliReceipt(
            SchemaVersion: 1,
            ReportFile: reservation.ReportFileName,
            Sha256: checksum,
            CreatedAtUtc: DateTimeOffset.UtcNow);
        var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(
            receipt,
            JsonDefaults.Indented);
        await reservation.WriteAsync(
            bytes,
            receiptBytes,
            cancellationToken);
    }

    private static bool TryParse(
        string[] args,
        out CommandRequest request)
    {
        request = default;
        if (args.Length < 3 ||
            args[2].Length is 0 or > 1024 ||
            !Path.IsPathFullyQualified(args[2]))
        {
            return false;
        }

        if (string.Equals(
                args[1],
                "report",
                StringComparison.Ordinal))
        {
            if (args.Length != 3)
            {
                return false;
            }

            request = new CommandRequest(
                Repair: false,
                args[2],
                MaximumRepairs: 0);
            return true;
        }

        if (!string.Equals(
                args[1],
                "repair-expired-outbox",
                StringComparison.Ordinal))
        {
            return false;
        }

        var allowRepair = false;
        var maximumSpecified = false;
        var maximumRepairs = 100;
        for (var index = 3; index < args.Length; index++)
        {
            if (string.Equals(
                    args[index],
                    "--allow-repair",
                    StringComparison.Ordinal))
            {
                if (allowRepair)
                {
                    return false;
                }

                allowRepair = true;
                continue;
            }

            if (string.Equals(
                    args[index],
                    "--max",
                    StringComparison.Ordinal) &&
                !maximumSpecified &&
                index + 1 < args.Length &&
                int.TryParse(
                    args[++index],
                    out maximumRepairs) &&
                maximumRepairs is >= 1 and <= 500)
            {
                maximumSpecified = true;
                continue;
            }

            return false;
        }

        if (!allowRepair)
        {
            return false;
        }

        request = new CommandRequest(
            Repair: true,
            args[2],
            maximumRepairs);
        return true;
    }

    private static int ReadInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"{name} must be an integer.");
    }

    private static int ReadRequiredInt(
        string name,
        int unusedFallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) ||
            !int.TryParse(value, out var parsed))
        {
            throw new InvalidDataException(
                $"{name} is required and must be an integer.");
        }

        return parsed;
    }

    private static string ToStatus(
        ReconciliationRunStatus status) =>
        status switch
        {
            ReconciliationRunStatus.Completed => "completed",
            ReconciliationRunStatus.Truncated => "truncated",
            ReconciliationRunStatus.TimedOut => "timed_out",
            _ => "unknown"
        };

    private readonly record struct CommandRequest(
        bool Repair,
        string OutputPath,
        int MaximumRepairs);

    private sealed record CliReport(
        int SchemaVersion,
        string CommandMode,
        string ReconciliationMode,
        string Status,
        DateTimeOffset StartedAtUtc,
        long DurationMilliseconds,
        int CharacterRowsScanned,
        int OutboxRowsScanned,
        bool Truncated,
        IReadOnlyList<CliFinding> Findings,
        int? RecoveredExpiredOutboxLeases,
        bool? RepairLimitReached);

    private sealed record CliFinding(
        string Category,
        long Count);

    private sealed record CliReceipt(
        int SchemaVersion,
        string ReportFile,
        string Sha256,
        DateTimeOffset CreatedAtUtc);

    private sealed class ReportReservation : IAsyncDisposable
    {
        private readonly string _reportPath;
        private readonly string _receiptPath;
        private readonly FileStream _report;
        private readonly FileStream _receipt;
        private bool _deleteOnDispose = true;

        private ReportReservation(
            string reportPath,
            string receiptPath,
            FileStream report,
            FileStream receipt)
        {
            _reportPath = reportPath;
            _receiptPath = receiptPath;
            _report = report;
            _receipt = receipt;
        }

        public string ReportFileName =>
            Path.GetFileName(_reportPath);

        public static ReportReservation? TryCreate(
            string reportPath)
        {
            var receiptPath = reportPath + ".receipt.json";
            FileStream? report = null;
            try
            {
                report = OpenReservation(reportPath);
                var receipt = OpenReservation(receiptPath);
                return new ReportReservation(
                    reportPath,
                    receiptPath,
                    report,
                    receipt);
            }
            catch (Exception exception)
                when (exception is IOException or
                    UnauthorizedAccessException or
                    ArgumentException or
                    NotSupportedException or
                    System.Security.SecurityException)
            {
                report?.Dispose();
                if (report is not null)
                {
                    File.Delete(reportPath);
                }

                return null;
            }
        }

        public void Preserve() => _deleteOnDispose = false;

        public async Task WriteAsync(
            byte[] report,
            byte[] receipt,
            CancellationToken cancellationToken)
        {
            await WriteAsync(_report, report, cancellationToken);
            await WriteAsync(_receipt, receipt, cancellationToken);
        }

        public async Task TryWriteFailureEvidenceAsync(
            string failureType,
            CancellationToken cancellationToken)
        {
            try
            {
                var report = JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        schemaVersion = 1,
                        commandMode = "repair_expired_outbox",
                        status = "failed",
                        failureType
                    },
                    JsonDefaults.Indented);
                var checksum =
                    Convert.ToHexString(SHA256.HashData(report));
                var receipt = JsonSerializer.SerializeToUtf8Bytes(
                    new CliReceipt(
                        1,
                        ReportFileName,
                        checksum,
                        DateTimeOffset.UtcNow),
                    JsonDefaults.Indented);
                await WriteAsync(
                    report,
                    receipt,
                    cancellationToken);
            }
            catch
            {
                // The exclusive empty reservations remain as evidence that
                // an explicitly authorized repair attempt began.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _report.DisposeAsync();
            await _receipt.DisposeAsync();
            if (_deleteOnDispose)
            {
                File.Delete(_reportPath);
                File.Delete(_receiptPath);
            }
        }

        private static FileStream OpenReservation(string path) =>
            new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
                FileOptions.Asynchronous |
                FileOptions.WriteThrough);

        private static async Task WriteAsync(
            FileStream stream,
            byte[] content,
            CancellationToken cancellationToken)
        {
            stream.Position = 0;
            stream.SetLength(0);
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
    }
}
