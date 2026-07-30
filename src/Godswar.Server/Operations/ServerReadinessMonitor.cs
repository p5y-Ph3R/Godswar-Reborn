using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.Operations;

internal sealed class ServerReadinessMonitorOptions
{
    public int PollIntervalMilliseconds { get; set; } = 1_000;

    public int DatabaseTimeoutMilliseconds { get; set; } = 750;

    public int MaximumWorkerHeartbeatAgeMilliseconds { get; set; } =
        15_000;

    public int MaximumCheckpointOldestAgeMilliseconds { get; set; } =
        60_000;

    public TimeSpan PollInterval =>
        TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan DatabaseTimeout =>
        TimeSpan.FromMilliseconds(DatabaseTimeoutMilliseconds);

    public TimeSpan MaximumWorkerHeartbeatAge =>
        TimeSpan.FromMilliseconds(
            MaximumWorkerHeartbeatAgeMilliseconds);

    public TimeSpan MaximumCheckpointOldestAge =>
        TimeSpan.FromMilliseconds(
            MaximumCheckpointOldestAgeMilliseconds);

    public void Validate()
    {
        RequireRange(
            PollIntervalMilliseconds,
            100,
            10_000,
            nameof(PollIntervalMilliseconds));
        RequireRange(
            DatabaseTimeoutMilliseconds,
            50,
            5_000,
            nameof(DatabaseTimeoutMilliseconds));
        RequireRange(
            MaximumWorkerHeartbeatAgeMilliseconds,
            1_000,
            300_000,
            nameof(MaximumWorkerHeartbeatAgeMilliseconds));
        RequireRange(
            MaximumCheckpointOldestAgeMilliseconds,
            1_000,
            3_600_000,
            nameof(MaximumCheckpointOldestAgeMilliseconds));
    }

    private static void RequireRange(
        int value,
        int minimum,
        int maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"Operations.Readiness.{name} must be between " +
                $"{minimum} and {maximum}.");
        }
    }
}

internal sealed class ServerReadinessMonitor
{
    private readonly CharacterCheckpointCoordinator _checkpoints;
    private readonly GameSessionRegistry _registry;
    private readonly ServerReadinessMonitorOptions _options;
    private readonly PostgresApplicationDataRuntime? _postgres;
    private readonly bool _requireOutbox;
    private readonly bool _requireZodiac;
    private readonly Action<ServerOperationalSnapshot>? _stateObserver;
    private readonly SecureUdpRuntime? _secureUdp;
    private readonly ServerOperationalState _state;
    private readonly TaskCompletionSource _firstRefresh =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _observedStateVersion = -1;

    public ServerReadinessMonitor(
        ServerOperationalState state,
        ServerReadinessMonitorOptions options,
        CharacterCheckpointCoordinator checkpoints,
        GameSessionRegistry registry,
        PostgresApplicationDataRuntime? postgres,
        bool requireOutbox,
        bool requireZodiac,
        SecureUdpRuntime? secureUdp,
        Action<ServerOperationalSnapshot>? stateObserver = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _checkpoints = checkpoints ??
            throw new ArgumentNullException(nameof(checkpoints));
        _registry = registry ?? throw new ArgumentNullException(
            nameof(registry));
        _postgres = postgres;
        _requireOutbox = requireOutbox;
        _requireZodiac = requireZodiac;
        _secureUdp = secureUdp;
        _stateObserver = stateObserver;
    }

    public Task WaitUntilFirstRefreshAsync(
        CancellationToken cancellationToken = default) =>
        _firstRefresh.Task.WaitAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            while (true)
            {
                await RefreshAsync(cancellationToken);
                _firstRefresh.TrySetResult();
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _firstRefresh.TrySetException(error);
            throw;
        }
    }

    internal async Task RefreshAsync(
        CancellationToken cancellationToken)
    {
        _state.SetDependency(
            ServerReadinessDependency.Database,
            await CheckDatabaseAsync(cancellationToken));

        var checkpoint = _checkpoints.GetSnapshot();
        var checkpointReady =
            checkpoint.State == CharacterCheckpointRuntimeState.Ready &&
            checkpoint.HeartbeatAge <=
                _options.MaximumWorkerHeartbeatAge &&
            checkpoint.OldestPendingAge <=
                _options.MaximumCheckpointOldestAge;
        _state.SetDependency(
            ServerReadinessDependency.CheckpointWorker,
            checkpointReady);

        var progression =
            _registry.GetDurableProgressionRetrySnapshot();
        var progressionReady = !progression.Enabled ||
            (progression.State ==
                DurableProgressionRetryWorkerState.Running &&
             progression.HeartbeatAge <=
                _options.MaximumWorkerHeartbeatAge);
        var outbox = PostgresCommandMetrics.GetSnapshot();
        var outboxReady = !_requireOutbox ||
            (outbox.State == OutboxDispatcherState.Running &&
             outbox.HeartbeatAge <=
                _options.MaximumWorkerHeartbeatAge);
        _state.SetDependency(
            ServerReadinessDependency.PersistenceWorkers,
            progressionReady && outboxReady);

        _state.SetDependency(
            ServerReadinessDependency.BoundedQueues,
            checkpoint.PendingKeys < checkpoint.Capacity &&
            progression.QueueDepth < progression.Capacity);
        _state.SetDependency(
            ServerReadinessDependency.SecureUdp,
            _secureUdp?.GetSnapshot().IsReady ?? true);
        _state.SetDependency(
            ServerReadinessDependency.SimulationLoops,
            AreRequiredSimulationLoopsReady());
        ObserveStateChange();
    }

    private async Task<bool> CheckDatabaseAsync(
        CancellationToken cancellationToken)
    {
        if (_postgres is null)
        {
            return true;
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(_options.DatabaseTimeout);
        try
        {
            return await _postgres.CheckHealthAsync(timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void ObserveStateChange()
    {
        var snapshot = _state.GetSnapshot();
        if (snapshot.Version == Interlocked.Read(
                ref _observedStateVersion))
        {
            return;
        }

        Interlocked.Exchange(
            ref _observedStateVersion,
            snapshot.Version);
        try
        {
            _stateObserver?.Invoke(snapshot);
        }
        catch
        {
            // Telemetry observers cannot alter readiness publication.
        }
    }

    private bool AreRequiredSimulationLoopsReady()
    {
        Span<SimulationLoopKind> required =
        [
            SimulationLoopKind.MonsterWorld,
            SimulationLoopKind.PlayerRecovery,
            SimulationLoopKind.ExperienceBoostReconciliation,
            SimulationLoopKind.ZodiacEnergyAccrual
        ];
        var count = _requireZodiac ? required.Length : required.Length - 1;
        for (var index = 0; index < count; index++)
        {
            if (!IsSimulationLoopReady(
                    SimulationLoopMetrics.GetRuntimeSnapshot(
                        required[index]),
                    _options.MaximumWorkerHeartbeatAge))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool IsSimulationLoopReady(
        SimulationLoopRuntimeSnapshot snapshot,
        TimeSpan heartbeatGrace)
    {
        if (snapshot.ActiveLoops <= 0 ||
            snapshot.ExpectedPeriod <= TimeSpan.Zero ||
            snapshot.HeartbeatAge < TimeSpan.Zero)
        {
            return false;
        }

        var maximumAge = snapshot.ExpectedPeriod >=
            TimeSpan.MaxValue - heartbeatGrace
                ? TimeSpan.MaxValue
                : snapshot.ExpectedPeriod + heartbeatGrace;
        return snapshot.HeartbeatAge <= maximumAge;
    }
}
