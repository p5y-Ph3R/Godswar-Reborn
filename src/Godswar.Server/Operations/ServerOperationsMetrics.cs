using System.Diagnostics.Metrics;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.Operations;

internal sealed class ServerOperationsMetrics : IDisposable
{
    public const string MeterName = "Godswar.Server.Operations";

    private readonly Meter _meter = new(MeterName);
    private readonly ManagementRequestObserver? _managementObserver;
    private readonly ServerOperationalState _state;
    private readonly CriticalTaskSupervisor _tasks;
    private readonly Counter<long> _managementRequests;

    public ServerOperationsMetrics(
        ServerOperationalState state,
        CriticalTaskSupervisor tasks,
        ManagementRequestObserver? managementObserver = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _managementObserver = managementObserver;
        _managementRequests = _meter.CreateCounter<long>(
            "godswar.server.operations.management.requests",
            "{request}",
            "Management requests by finite route and outcome.");
        _meter.CreateObservableGauge(
            "godswar.server.operations.readiness",
            ObserveReadiness,
            description:
            "Process readiness with finite phase and reason labels.");
        _meter.CreateObservableGauge(
            "godswar.server.operations.liveness",
            () => _state.GetSnapshot().IsLive ? 1 : 0,
            description: "Whether the game-server process is live.");
        _meter.CreateObservableGauge(
            "godswar.server.operations.critical_tasks",
            ObserveCriticalTasks,
            description:
            "Registered critical tasks by finite task and state.");
    }

    public void RecordManagement(
        ManagementRequestObservation observation)
    {
        _managementRequests.Add(
            1,
            new("route", observation.Route.ToProtocolValue()),
            new("outcome", observation.Outcome.ToProtocolValue()));
        ServerActivity.RecordCompleted(
            ServerTraceOperation.ManagementRequest,
            TimeSpan.Zero,
            TraceOutcome(observation.Outcome),
            System.Diagnostics.ActivityKind.Server,
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Component,
                observation.Route.ToProtocolValue()),
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Reason,
                observation.Outcome.ToProtocolValue()));
        try
        {
            _managementObserver?.Invoke(observation);
        }
        catch
        {
            // Logging cannot alter management request handling.
        }
    }

    public void Dispose() => _meter.Dispose();

    private Measurement<int> ObserveReadiness()
    {
        var snapshot = _state.GetSnapshot();
        return new Measurement<int>(
            snapshot.IsReady ? 1 : 0,
            new("phase", snapshot.Phase.ToProtocolValue()),
            new(
                "reason",
                snapshot.ReadinessReason.ToProtocolValue()));
    }

    private IEnumerable<Measurement<int>> ObserveCriticalTasks()
    {
        foreach (var task in _tasks.GetSnapshot().Tasks)
        {
            yield return new Measurement<int>(
                task.State == CriticalTaskState.Running ? 1 : 0,
                new("task", task.Kind.ToProtocolValue()),
                new("state", task.State.ToProtocolValue()));
        }
    }

    private static ServerTraceOutcome TraceOutcome(
        ManagementRequestOutcome outcome) =>
        outcome switch
        {
            ManagementRequestOutcome.Success =>
                ServerTraceOutcome.Accepted,
            ManagementRequestOutcome.NotReady or
            ManagementRequestOutcome.Unauthorized or
            ManagementRequestOutcome.Rejected or
            ManagementRequestOutcome.BadRequest or
            ManagementRequestOutcome.HeadersTooLarge or
            ManagementRequestOutcome.NotFound or
            ManagementRequestOutcome.Overloaded or
            ManagementRequestOutcome.NotLive =>
                ServerTraceOutcome.Rejected,
            _ => ServerTraceOutcome.Faulted
        };
}
