using System.Text.Json;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Reconciliation;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B19ReconciliationWorkerChecks
{
    internal const string CheckName =
        "B19 reconciliation worker and configuration safety";

    private static readonly string[] EnvironmentVariables =
    [
        "GODSWAR_RECONCILIATION_ENABLED",
        "GODSWAR_RECONCILIATION_MODE",
        "GODSWAR_RECONCILIATION_BATCH_SIZE",
        "GODSWAR_RECONCILIATION_MAXIMUM_CHARACTERS_PER_RUN",
        "GODSWAR_RECONCILIATION_MAXIMUM_OUTBOX_EVENTS_PER_RUN",
        "GODSWAR_RECONCILIATION_POLL_INTERVAL_MILLISECONDS",
        "GODSWAR_RECONCILIATION_COMMAND_TIMEOUT_MILLISECONDS",
        "GODSWAR_RECONCILIATION_RUN_TIMEOUT_MILLISECONDS"
    ];

    public static async Task RunAsync()
    {
        await CheckDisabledWorkerAsync();
        await CheckFirstPassReadinessAsync();
        await CheckCompletedFindingRemainsVisibleAsync();
        await CheckTruncationIsProgressNotReadinessAsync();
        await CheckTimeoutDoesNotHeartbeatAsync();
        await CheckFailureDoesNotHeartbeatAsync();
        CheckConfigurationFailsClosed();
    }

    private static async Task CheckDisabledWorkerAsync()
    {
        var options = Options(enabled: false);
        var worker = new PostgresReconciliationWorker(
            new ReconciliationRunner(
                new TerminalReader(),
                options),
            options);

        var before = worker.GetSnapshot();
        Check.True(
            !before.Enabled &&
            before.State == ReconciliationWorkerState.Disabled &&
            !before.FirstPassCompleted,
            "disabled reconciliation remains outside readiness");
        await worker.RunAsync();
        var after = worker.GetSnapshot();
        Check.Equal(
            (int)ReconciliationWorkerState.Disabled,
            (int)after.State,
            "disabled worker starts no background loop");
    }

    private static async Task CheckFirstPassReadinessAsync()
    {
        var options = Options(enabled: true);
        var worker = new PostgresReconciliationWorker(
            new ReconciliationRunner(
                new TerminalReader(),
                options),
            options);
        using var shutdown = new CancellationTokenSource();
        var run = worker.RunAsync(shutdown.Token);
        var ready = await WaitForSnapshotAsync(
            worker,
            snapshot => snapshot.FirstPassCompleted);

        Check.True(
            ready.Enabled &&
            ready.State == ReconciliationWorkerState.Running &&
            ready.LastRunStatus == ReconciliationRunStatus.Completed &&
            ready.HeartbeatAge < TimeSpan.FromSeconds(2) &&
            ready.HeartbeatAge <= ready.MaximumHealthyHeartbeatAge,
            "enabled worker becomes healthy only after its first pass");
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(
            (int)ReconciliationWorkerState.Stopped,
            (int)worker.GetSnapshot().State,
            "worker cancellation is a clean stop");
    }

    private static async Task CheckTimeoutDoesNotHeartbeatAsync()
    {
        var options = Options(enabled: true);
        options.CommandTimeoutMilliseconds = 100;
        options.RunTimeoutMilliseconds = 100;
        var worker = new PostgresReconciliationWorker(
            new ReconciliationRunner(
                new BlockingReader(),
                options),
            options);
        using var shutdown = new CancellationTokenSource();
        var run = worker.RunAsync(shutdown.Token);
        var timedOut = await WaitForSnapshotAsync(
            worker,
            snapshot =>
                snapshot.LastRunStatus ==
                    ReconciliationRunStatus.TimedOut);

        Check.True(
            !timedOut.FirstPassCompleted &&
            timedOut.HeartbeatAge == TimeSpan.MaxValue,
            "timed-out pass cannot establish a healthy heartbeat");
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task CheckTruncationIsProgressNotReadinessAsync()
    {
        var options = Options(enabled: true);
        options.MaximumCharactersPerRun = 1;
        var worker = new PostgresReconciliationWorker(
            new ReconciliationRunner(
                new TruncatingReader(),
                options),
            options);
        using var shutdown = new CancellationTokenSource();
        var run = worker.RunAsync(shutdown.Token);
        var truncated = await WaitForSnapshotAsync(
            worker,
            snapshot =>
                snapshot.LastRunStatus ==
                    ReconciliationRunStatus.Truncated);

        Check.True(
            !truncated.FirstPassCompleted &&
            truncated.LastRunTruncated &&
            truncated.HeartbeatAge < TimeSpan.FromSeconds(2),
            "a truncated pass refreshes liveness but cannot establish " +
            "first-pass readiness");
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task CheckFailureDoesNotHeartbeatAsync()
    {
        var options = Options(enabled: true);
        var reader = new FailingReader();
        var worker = new PostgresReconciliationWorker(
            new ReconciliationRunner(reader, options),
            options);
        using var shutdown = new CancellationTokenSource();
        var run = worker.RunAsync(shutdown.Token);
        await reader.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failed = worker.GetSnapshot();

        Check.True(
            failed.State == ReconciliationWorkerState.Running &&
            !failed.FirstPassCompleted &&
            failed.LastRunStatus is null &&
            failed.HeartbeatAge == TimeSpan.MaxValue,
            "database failure cannot refresh worker health");
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void CheckConfigurationFailsClosed()
    {
        using var environment =
            new EnvironmentScope(EnvironmentVariables);
        foreach (var name in EnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"godswar-b19-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var disabledPath = WriteOptions(
                directory,
                "disabled.json",
                provider: "Postgres",
                enabled: false);
            Check.True(
                !ServerOptions.Load(disabledPath)
                    .Storage.Reconciliation.Enabled,
                "PostgreSQL storage keeps reconciliation disabled by default");

            var retiredJsonPath = WriteOptions(
                directory,
                "retired-json.json",
                provider: "Json",
                enabled: true);
            ExpectInvalidData(
                () => ServerOptions.Load(retiredJsonPath),
                "requires PostgreSQL",
                "retired JSON authority fails closed at the reconciliation boundary");

            Environment.SetEnvironmentVariable(
                "GODSWAR_RECONCILIATION_MODE",
                "RepairEconomy");
            ExpectInvalidData(
                () => ServerOptions.Load(disabledPath),
                "mode is invalid",
                "unknown repair mode fails closed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ReconciliationOptions Options(bool enabled) =>
        new()
        {
            Enabled = enabled,
            BatchSize = 2,
            MaximumCharactersPerRun = 10,
            MaximumOutboxEventsPerRun = 10,
            PollIntervalMilliseconds = 10_000,
            CommandTimeoutMilliseconds = 250,
            RunTimeoutMilliseconds = 2_000
        };

    private static async Task<ReconciliationWorkerSnapshot>
        WaitForSnapshotAsync(
            PostgresReconciliationWorker worker,
            Func<ReconciliationWorkerSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = worker.GetSnapshot();
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private static string WriteOptions(
        string directory,
        string name,
        string provider,
        bool enabled)
    {
        var root = JsonNode.Parse(
            File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "appsettings.json")))!.AsObject();
        root["storage"]!["provider"] = provider;
        root["authentication"]!["allowLegacyRawAuthentication"] = true;
        root["storage"]!["postgresConnectionString"] =
            provider == "Postgres"
                ? "Host=127.0.0.1;Database=b19_options"
                : string.Empty;
        root["storage"]!["reconciliation"] = new JsonObject
        {
            ["enabled"] = enabled,
            ["mode"] = "ReportOnly",
            ["batchSize"] = 100,
            ["maximumCharactersPerRun"] = 5000,
            ["maximumOutboxEventsPerRun"] = 5000,
            ["pollIntervalMilliseconds"] = 300000,
            ["commandTimeoutMilliseconds"] = 5000,
            ["runTimeoutMilliseconds"] = 30000
        };
        var path = Path.Combine(directory, name);
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        return path;
    }

    private static void ExpectInvalidData(
        Action action,
        string expected,
        string description)
    {
        try
        {
            action();
        }
        catch (InvalidDataException exception)
        {
            Check.True(
                exception.Message.Contains(
                    expected,
                    StringComparison.OrdinalIgnoreCase),
                $"{description} reports its policy");
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected InvalidDataException.");
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

    private sealed class TerminalReader : IReconciliationReader
    {
        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReconciliationSnapshot>(
                new TerminalSnapshot());
    }

    private sealed class TerminalSnapshot : IReconciliationSnapshot
    {
        public Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationPage(0, 0, true, []));

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationPage(0, 0, true, []));

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationOutboxPositionPage(
                after,
                0,
                true,
                []));

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingReader : IReconciliationReader
    {
        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReconciliationSnapshot>(
                new BlockingSnapshot());
    }

    private sealed class TruncatingReader : IReconciliationReader
    {
        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReconciliationSnapshot>(
                new TruncatingSnapshot());
    }

    private sealed class TruncatingSnapshot : IReconciliationSnapshot
    {
        public Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationPage(
                afterCharacterKey + 1,
                1,
                false,
                []));

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationPage(
                afterOutboxKey,
                0,
                true,
                []));

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationOutboxPositionPage(
                after,
                0,
                true,
                []));

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSnapshot : IReconciliationSnapshot
    {
        public async Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unreachable.");

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unreachable.");

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingReader : IReconciliationReader
    {
        public TaskCompletionSource Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken)
        {
            Called.TrySetResult();
            throw new TimeoutException("Expected B19 test failure.");
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _values;

        public EnvironmentScope(IEnumerable<string> names)
        {
            _values = names.ToDictionary(
                static name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        }

        public void Dispose()
        {
            foreach (var pair in _values)
            {
                Environment.SetEnvironmentVariable(
                    pair.Key,
                    pair.Value);
            }
        }
    }
}
