namespace Godswar.Server.Operations;

internal enum CriticalTaskKind : byte
{
    ManagementHttp = 1,
    LoginListener = 2,
    GameListener = 3,
    SecureUdp = 4,
    CheckpointWorker = 5,
    OutboxDispatcher = 6,
    MonsterWorld = 7,
    PlayerRecovery = 8,
    ExperienceBoostReconciliation = 9,
    ZodiacEnergyAccrual = 10,
    DurableProgressionRetry = 11,
    PostgresReadiness = 12
}

internal enum CriticalTaskState : byte
{
    Running = 1,
    Stopped = 2,
    Faulted = 3
}

internal readonly record struct CriticalTaskSnapshot(
    CriticalTaskKind Kind,
    CriticalTaskState State);

internal readonly record struct CriticalTaskSupervisorSnapshot(
    bool RegistrationsSealed,
    int RegisteredTasks,
    int RunningTasks,
    CriticalTaskKind? FaultedTask,
    IReadOnlyList<CriticalTaskSnapshot> Tasks);

internal sealed class CriticalTaskStoppedException : Exception
{
    public CriticalTaskStoppedException(CriticalTaskKind task)
        : base(
            $"Critical task '{task}' completed before host cancellation.")
    {
        Task = task;
    }

    public CriticalTaskKind Task { get; }
}

/// <summary>
/// Converts faults and unexpected successful completion into one finite host
/// failure signal. Only tasks expected to live until cancellation belong here.
/// </summary>
internal sealed class CriticalTaskSupervisor
{
    private readonly Action _requestShutdown;
    private readonly Action<CriticalTaskSnapshot>? _observer;
    private readonly ServerOperationalState _operationalState;
    private readonly Dictionary<CriticalTaskKind, CriticalTaskState> _tasks =
        [];
    private readonly object _sync = new();
    private CriticalTaskKind? _faultedTask;
    private bool _registrationsSealed;

    public CriticalTaskSupervisor(
        ServerOperationalState operationalState,
        Action requestShutdown,
        Action<CriticalTaskSnapshot>? observer = null)
    {
        _operationalState = operationalState ??
            throw new ArgumentNullException(nameof(operationalState));
        _requestShutdown = requestShutdown ??
            throw new ArgumentNullException(nameof(requestShutdown));
        _observer = observer;
        _operationalState.SetDependency(
            ServerReadinessDependency.CriticalTasks,
            ready: false);
    }

    public async Task RunAsync(
        CriticalTaskKind kind,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RegisterRunning(kind);

        try
        {
            await operation(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new CriticalTaskStoppedException(kind);
            }

            MarkStopped(kind);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            MarkStopped(kind);
        }
        catch
        {
            MarkFaulted(kind);
            throw;
        }
    }

    public void SealRegistrations()
    {
        lock (_sync)
        {
            if (_registrationsSealed)
            {
                throw new InvalidOperationException(
                    "Critical task registrations are already sealed.");
            }
            if (_tasks.Count == 0)
            {
                throw new InvalidOperationException(
                    "At least one critical task must be registered.");
            }

            _registrationsSealed = true;
            PublishReadinessLocked();
        }
    }

    public CriticalTaskSupervisorSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var tasks = _tasks
                .OrderBy(static entry => entry.Key)
                .Select(static entry =>
                    new CriticalTaskSnapshot(entry.Key, entry.Value))
                .ToArray();
            return new CriticalTaskSupervisorSnapshot(
                _registrationsSealed,
                tasks.Length,
                tasks.Count(static task =>
                    task.State == CriticalTaskState.Running),
                _faultedTask,
                tasks);
        }
    }

    private void RegisterRunning(CriticalTaskKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        lock (_sync)
        {
            if (_registrationsSealed)
            {
                throw new InvalidOperationException(
                    "Critical task registrations are sealed.");
            }
            if (!_tasks.TryAdd(kind, CriticalTaskState.Running))
            {
                throw new InvalidOperationException(
                    $"Critical task '{kind}' is already registered.");
            }
        }
        Observe(kind, CriticalTaskState.Running);
    }

    private void MarkStopped(CriticalTaskKind kind)
    {
        var changed = false;
        lock (_sync)
        {
            if (!_tasks.TryGetValue(kind, out var current) ||
                current == CriticalTaskState.Faulted)
            {
                return;
            }

            _tasks[kind] = CriticalTaskState.Stopped;
            changed = true;
            PublishReadinessLocked();
        }
        if (changed)
        {
            Observe(kind, CriticalTaskState.Stopped);
        }
    }

    private void MarkFaulted(CriticalTaskKind kind)
    {
        var notify = false;
        lock (_sync)
        {
            if (_tasks.TryGetValue(kind, out var current) &&
                current != CriticalTaskState.Faulted)
            {
                _tasks[kind] = CriticalTaskState.Faulted;
                _faultedTask ??= kind;
                notify = true;
            }
            PublishReadinessLocked();
        }

        if (!notify)
        {
            return;
        }

        Observe(kind, CriticalTaskState.Faulted);
        _operationalState.MarkCriticalTaskFaulted();
        try
        {
            _requestShutdown();
        }
        catch
        {
            // The initiating task failure remains the authoritative exception.
        }
    }

    private void PublishReadinessLocked()
    {
        var ready = _registrationsSealed &&
            _faultedTask is null &&
            _tasks.Count > 0 &&
            _tasks.Values.All(static state =>
                state == CriticalTaskState.Running);
        _operationalState.SetDependency(
            ServerReadinessDependency.CriticalTasks,
            ready);
    }

    private void Observe(
        CriticalTaskKind kind,
        CriticalTaskState state)
    {
        try
        {
            _observer?.Invoke(new CriticalTaskSnapshot(kind, state));
        }
        catch
        {
            // Telemetry observers cannot alter task supervision.
        }
    }
}

internal static class CriticalTaskCodes
{
    public static string ToProtocolValue(this CriticalTaskKind task) =>
        task switch
        {
            CriticalTaskKind.ManagementHttp => "management_http",
            CriticalTaskKind.LoginListener => "login_listener",
            CriticalTaskKind.GameListener => "game_listener",
            CriticalTaskKind.SecureUdp => "secure_udp",
            CriticalTaskKind.CheckpointWorker => "checkpoint_worker",
            CriticalTaskKind.OutboxDispatcher => "outbox_dispatcher",
            CriticalTaskKind.MonsterWorld => "monster_world",
            CriticalTaskKind.PlayerRecovery => "player_recovery",
            CriticalTaskKind.ExperienceBoostReconciliation =>
                "experience_boost_reconciliation",
            CriticalTaskKind.ZodiacEnergyAccrual =>
                "zodiac_energy_accrual",
            CriticalTaskKind.DurableProgressionRetry =>
                "durable_progression_retry",
            CriticalTaskKind.PostgresReadiness =>
                "postgres_readiness",
            _ => throw new ArgumentOutOfRangeException(nameof(task))
        };

    public static string ToProtocolValue(this CriticalTaskState state) =>
        state switch
        {
            CriticalTaskState.Running => "running",
            CriticalTaskState.Stopped => "stopped",
            CriticalTaskState.Faulted => "faulted",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
}
