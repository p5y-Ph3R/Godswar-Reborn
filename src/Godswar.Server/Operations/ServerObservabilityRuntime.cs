using System.Text;
using System.Text.Json;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.Operations;

internal sealed class ServerObservabilityRuntime : IDisposable
{
    private const int TraceResponseSpanLimit = 64;
    private const string LocalGameplayLogsEnvironmentVariable =
        "GODSWAR_LOCAL_GAMEPLAY_LOGS";

    private readonly StructuredConsoleBoundary? _consoleBoundary;
    private readonly BoundedPrometheusCollector _metrics;
    private readonly BoundedStructuredLogger _logger;
    private readonly int _maximumPayloadBytes;
    private readonly BoundedTraceBuffer _traces;
    private int _disposed;
    private long _reportedCollectorPressure;

    private ServerObservabilityRuntime(
        BoundedStructuredLogger logger,
        StructuredConsoleBoundary? consoleBoundary,
        BoundedPrometheusCollector metrics,
        BoundedTraceBuffer traces,
        int maximumPayloadBytes)
    {
        _logger = logger;
        _consoleBoundary = consoleBoundary;
        _metrics = metrics;
        _traces = traces;
        _maximumPayloadBytes = maximumPayloadBytes;
    }

    public BoundedStructuredLogger Logger => _logger;

    public static ServerObservabilityRuntime Start(
        int maximumManagementResponseBytes,
        bool installConsoleBoundary)
    {
        var maximumPayloadBytes = Math.Max(
            1_024,
            maximumManagementResponseBytes - 1_024);
        var metrics = new BoundedPrometheusCollector(
            new PrometheusCollectorOptions
            {
                MaximumSnapshotBytes = maximumPayloadBytes
            });
        var traces = new BoundedTraceBuffer();
        var logOptions = new StructuredLogOptions();
        var logger = new BoundedStructuredLogger(
            Console.Out,
            logOptions);
        var boundary = ShouldInstallConsoleBoundary(
                installConsoleBoundary,
                Environment.GetEnvironmentVariable(
                    "GODSWAR_RUNTIME_PROFILE"),
                Environment.GetEnvironmentVariable(
                    LocalGameplayLogsEnvironmentVariable))
            ? StructuredConsoleBoundary.Install(logger, logOptions)
            : null;
        return new ServerObservabilityRuntime(
            logger,
            boundary,
            metrics,
            traces,
            maximumPayloadBytes);
    }

    internal static bool ShouldInstallConsoleBoundary(
        bool requested,
        string? runtimeProfile,
        string? localGameplayLogs) =>
        requested &&
        !(string.Equals(
              runtimeProfile,
              nameof(ServerRuntimeProfileKind.LocalDevelopment),
              StringComparison.OrdinalIgnoreCase) &&
          string.Equals(
              localGameplayLogs,
              "true",
              StringComparison.OrdinalIgnoreCase));

    public void RecordLifecycle(
        string component,
        string outcome,
        OperationalLogLevel level =
            OperationalLogLevel.Information)
    {
        _logger.TryWrite(
            OperationalLogEvent.ServerLifecycle,
            level,
            OperationalLogValue.FromCode(
                OperationalLogField.Component,
                component),
            OperationalLogValue.FromCode(
                OperationalLogField.Outcome,
                outcome));
    }

    public void RecordCriticalTask(CriticalTaskSnapshot task)
    {
        _logger.TryWrite(
            OperationalLogEvent.CriticalTaskState,
            task.State == CriticalTaskState.Faulted
                ? OperationalLogLevel.Critical
                : OperationalLogLevel.Information,
            OperationalLogValue.FromCode(
                OperationalLogField.Component,
                task.Kind.ToProtocolValue()),
            OperationalLogValue.FromCode(
                OperationalLogField.State,
                task.State.ToProtocolValue()));
    }

    public void RecordOperationalState(
        ServerOperationalSnapshot snapshot)
    {
        _logger.TryWrite(
            OperationalLogEvent.ReadinessChanged,
            snapshot.IsReady
                ? OperationalLogLevel.Information
                : OperationalLogLevel.Warning,
            OperationalLogValue.FromCode(
                OperationalLogField.Component,
                "server"),
            OperationalLogValue.FromCode(
                OperationalLogField.State,
                snapshot.Phase.ToProtocolValue()),
            OperationalLogValue.FromCode(
                OperationalLogField.Reason,
                snapshot.ReadinessReason.ToProtocolValue()));
    }

    public void RecordManagement(
        ManagementRequestObservation observation)
    {
        _logger.TryWrite(
            OperationalLogEvent.ManagementRequest,
            observation.Outcome == ManagementRequestOutcome.Success
                ? OperationalLogLevel.Debug
                : OperationalLogLevel.Warning,
            OperationalLogValue.FromCode(
                OperationalLogField.Component,
                observation.Route.ToProtocolValue()),
            OperationalLogValue.FromCode(
                OperationalLogField.Outcome,
                observation.Outcome.ToProtocolValue()));
    }

    public ValueTask<ManagementPayload> GetMetricsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = _metrics.CollectSnapshot();
        ReportCollectorPressure();
        return ValueTask.FromResult(
            new ManagementPayload(
                ManagementContentType.OpenMetricsText,
                Encoding.UTF8.GetBytes(content)));
    }

    public ValueTask<ManagementPayload> GetTracesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _traces.Snapshot();
        var take = Math.Min(snapshot.Length, TraceResponseSpanLimit);
        while (true)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                snapshot.AsSpan(snapshot.Length - take, take).ToArray());
            if (payload.Length <= _maximumPayloadBytes || take == 0)
            {
                return ValueTask.FromResult(
                    new ManagementPayload(
                        ManagementContentType.Json,
                        payload));
            }
            take /= 2;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _consoleBoundary?.Dispose();
        _logger.Dispose();
        _traces.Dispose();
        _metrics.Dispose();
    }

    private void ReportCollectorPressure()
    {
        var snapshot = _metrics.GetSnapshot();
        var pressure = snapshot.DroppedInstruments +
            snapshot.DroppedSeries +
            snapshot.DroppedTags +
            snapshot.DroppedMeasurements +
            snapshot.TruncatedSnapshots;
        var previous = Interlocked.Exchange(
            ref _reportedCollectorPressure,
            pressure);
        if (pressure <= previous)
        {
            return;
        }

        _logger.TryWrite(
            OperationalLogEvent.TelemetryExporter,
            OperationalLogLevel.Warning,
            OperationalLogValue.FromCode(
                OperationalLogField.Component,
                "metrics"),
            OperationalLogValue.FromCode(
                OperationalLogField.Outcome,
                "bounded_drop"),
            OperationalLogValue.FromNumber(
                OperationalLogField.Count,
                pressure - previous));
    }
}
