namespace Godswar.Server.Operations;

[Flags]
internal enum ServerReadinessDependency : ushort
{
    None = 0,
    ListenerProfile = 1 << 0,
    SchemaAndContent = 1 << 1,
    Database = 1 << 2,
    CheckpointWorker = 1 << 3,
    PersistenceWorkers = 1 << 4,
    CriticalTasks = 1 << 5,
    BoundedQueues = 1 << 6,
    SecureUdp = 1 << 7,
    SimulationLoops = 1 << 8,
    RedisCoordination = 1 << 9,
    All = ListenerProfile |
        SchemaAndContent |
        Database |
        CheckpointWorker |
        PersistenceWorkers |
        CriticalTasks |
        BoundedQueues |
        SecureUdp |
        SimulationLoops |
        RedisCoordination
}

internal enum ServerOperationalPhase : byte
{
    Starting = 1,
    Running = 2,
    Draining = 3,
    Stopping = 4,
    Faulted = 5,
    Stopped = 6
}

internal enum ServerReadinessReason : byte
{
    None = 0,
    Starting = 1,
    ListenerProfileNotReady = 2,
    SchemaOrContentNotReady = 3,
    DatabaseNotReady = 4,
    CheckpointWorkerNotReady = 5,
    PersistenceWorkerNotReady = 6,
    CriticalTaskNotReady = 7,
    QueueSaturated = 8,
    SecureUdpNotReady = 9,
    Draining = 10,
    Stopping = 11,
    CriticalTaskFaulted = 12,
    Stopped = 13,
    SimulationLoopNotReady = 14,
    RedisCoordinationNotReady = 15
}

internal readonly record struct ServerOperationalSnapshot(
    ServerOperationalPhase Phase,
    bool IsLive,
    bool IsReady,
    ServerReadinessReason ReadinessReason,
    ServerReadinessDependency RequiredDependencies,
    ServerReadinessDependency ReadyDependencies,
    long Version);

/// <summary>
/// Lock-protected cached state used by management requests. Setters never
/// perform dependency I/O; probes and workers publish their latest result.
/// </summary>
internal sealed class ServerOperationalState
{
    private readonly object _sync = new();
    private readonly ServerReadinessDependency _requiredDependencies;
    private ServerOperationalPhase _phase = ServerOperationalPhase.Starting;
    private ServerReadinessDependency _readyDependencies;
    private long _version;

    public ServerOperationalState(
        ServerReadinessDependency requiredDependencies)
    {
        if ((requiredDependencies & ~ServerReadinessDependency.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredDependencies));
        }

        _requiredDependencies = requiredDependencies;
    }

    public void SetDependency(
        ServerReadinessDependency dependency,
        bool ready)
    {
        ValidateSingleDependency(dependency);
        lock (_sync)
        {
            var updated = ready
                ? _readyDependencies | dependency
                : _readyDependencies & ~dependency;
            if (updated == _readyDependencies)
            {
                return;
            }

            _readyDependencies = updated;
            _version++;
        }
    }

    public bool TryMarkRunning()
    {
        lock (_sync)
        {
            if (_phase != ServerOperationalPhase.Starting)
            {
                return false;
            }

            _phase = ServerOperationalPhase.Running;
            _version++;
            return true;
        }
    }

    public bool TryBeginDrain()
    {
        lock (_sync)
        {
            if (_phase == ServerOperationalPhase.Draining)
            {
                return true;
            }
            if (_phase is not
                    ServerOperationalPhase.Starting and not
                    ServerOperationalPhase.Running)
            {
                return false;
            }

            _phase = ServerOperationalPhase.Draining;
            _version++;
            return true;
        }
    }

    public bool TryMarkStopping()
    {
        lock (_sync)
        {
            if (_phase == ServerOperationalPhase.Stopping)
            {
                return true;
            }
            if (_phase is ServerOperationalPhase.Faulted or
                ServerOperationalPhase.Stopped)
            {
                return false;
            }

            _phase = ServerOperationalPhase.Stopping;
            _version++;
            return true;
        }
    }

    public void MarkCriticalTaskFaulted()
    {
        lock (_sync)
        {
            if (_phase is ServerOperationalPhase.Faulted or
                ServerOperationalPhase.Stopped)
            {
                return;
            }

            _phase = ServerOperationalPhase.Faulted;
            _readyDependencies &=
                ~ServerReadinessDependency.CriticalTasks;
            _version++;
        }
    }

    public void MarkStopped()
    {
        lock (_sync)
        {
            if (_phase == ServerOperationalPhase.Stopped)
            {
                return;
            }

            _phase = ServerOperationalPhase.Stopped;
            _readyDependencies = ServerReadinessDependency.None;
            _version++;
        }
    }

    public ServerOperationalSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var isLive = _phase is
                ServerOperationalPhase.Starting or
                ServerOperationalPhase.Running or
                ServerOperationalPhase.Draining or
                ServerOperationalPhase.Stopping;
            var reason = ReadinessReasonLocked();
            return new ServerOperationalSnapshot(
                _phase,
                isLive,
                reason == ServerReadinessReason.None,
                reason,
                _requiredDependencies,
                _readyDependencies,
                _version);
        }
    }

    private ServerReadinessReason ReadinessReasonLocked()
    {
        switch (_phase)
        {
            case ServerOperationalPhase.Starting:
                return ServerReadinessReason.Starting;
            case ServerOperationalPhase.Draining:
                return ServerReadinessReason.Draining;
            case ServerOperationalPhase.Stopping:
                return ServerReadinessReason.Stopping;
            case ServerOperationalPhase.Faulted:
                return ServerReadinessReason.CriticalTaskFaulted;
            case ServerOperationalPhase.Stopped:
                return ServerReadinessReason.Stopped;
        }

        var missing = _requiredDependencies & ~_readyDependencies;
        if ((missing & ServerReadinessDependency.ListenerProfile) != 0)
        {
            return ServerReadinessReason.ListenerProfileNotReady;
        }
        if ((missing & ServerReadinessDependency.SchemaAndContent) != 0)
        {
            return ServerReadinessReason.SchemaOrContentNotReady;
        }
        if ((missing & ServerReadinessDependency.Database) != 0)
        {
            return ServerReadinessReason.DatabaseNotReady;
        }
        if ((missing & ServerReadinessDependency.CheckpointWorker) != 0)
        {
            return ServerReadinessReason.CheckpointWorkerNotReady;
        }
        if ((missing & ServerReadinessDependency.PersistenceWorkers) != 0)
        {
            return ServerReadinessReason.PersistenceWorkerNotReady;
        }
        if ((missing & ServerReadinessDependency.CriticalTasks) != 0)
        {
            return ServerReadinessReason.CriticalTaskNotReady;
        }
        if ((missing & ServerReadinessDependency.BoundedQueues) != 0)
        {
            return ServerReadinessReason.QueueSaturated;
        }
        if ((missing & ServerReadinessDependency.SecureUdp) != 0)
        {
            return ServerReadinessReason.SecureUdpNotReady;
        }
        if ((missing & ServerReadinessDependency.SimulationLoops) != 0)
        {
            return ServerReadinessReason.SimulationLoopNotReady;
        }
        if ((missing & ServerReadinessDependency.RedisCoordination) != 0)
        {
            return ServerReadinessReason.RedisCoordinationNotReady;
        }

        return ServerReadinessReason.None;
    }

    private static void ValidateSingleDependency(
        ServerReadinessDependency dependency)
    {
        var value = (ushort)dependency;
        if (dependency == ServerReadinessDependency.None ||
            (dependency & ~ServerReadinessDependency.All) != 0 ||
            (value & (value - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dependency));
        }
    }
}

internal static class ServerOperationalProtocolValues
{
    public static string ToProtocolValue(
        this ServerOperationalPhase phase) =>
        phase switch
        {
            ServerOperationalPhase.Starting => "starting",
            ServerOperationalPhase.Running => "running",
            ServerOperationalPhase.Draining => "draining",
            ServerOperationalPhase.Stopping => "stopping",
            ServerOperationalPhase.Faulted => "faulted",
            ServerOperationalPhase.Stopped => "stopped",
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };

    public static string ToProtocolValue(
        this ServerReadinessReason reason) =>
        reason switch
        {
            ServerReadinessReason.None => "none",
            ServerReadinessReason.Starting => "starting",
            ServerReadinessReason.ListenerProfileNotReady =>
                "listener_profile_not_ready",
            ServerReadinessReason.SchemaOrContentNotReady =>
                "schema_or_content_not_ready",
            ServerReadinessReason.DatabaseNotReady =>
                "database_not_ready",
            ServerReadinessReason.CheckpointWorkerNotReady =>
                "checkpoint_worker_not_ready",
            ServerReadinessReason.PersistenceWorkerNotReady =>
                "persistence_worker_not_ready",
            ServerReadinessReason.CriticalTaskNotReady =>
                "critical_task_not_ready",
            ServerReadinessReason.QueueSaturated =>
                "queue_saturated",
            ServerReadinessReason.SecureUdpNotReady =>
                "secure_udp_not_ready",
            ServerReadinessReason.Draining => "draining",
            ServerReadinessReason.Stopping => "stopping",
            ServerReadinessReason.CriticalTaskFaulted =>
                "critical_task_faulted",
            ServerReadinessReason.Stopped => "stopped",
            ServerReadinessReason.SimulationLoopNotReady =>
                "simulation_loop_not_ready",
            ServerReadinessReason.RedisCoordinationNotReady =>
                "redis_coordination_not_ready",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
}
