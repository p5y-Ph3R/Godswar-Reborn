using System.Net;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Networking;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class B13PersistenceWorkerChecks
{
    public static async Task RunAsync()
    {
        CheckReadinessOptions();
        CheckWorkerSnapshots();
        CheckSimulationLoopReadiness();
        await CheckDrainAsync();
        await CheckCriticalTaskShutdownAsync();
    }

    private static async Task CheckCriticalTaskShutdownAsync()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ShutdownCheckpointStore
        {
            PositionWrite = async (checkpoint, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return new CharacterCheckpointWriteResult(
                    CharacterCheckpointWriteStatus.Applied,
                    checkpoint.Revision);
            }
        };
        var options = new CharacterCheckpointWorkerOptions
        {
            QueueCapacity = 8,
            WorkerCount = 1,
            DirectOperationConcurrency = 1,
            DirectAdmissionTimeoutMilliseconds = 50,
            CommandTimeoutMilliseconds = 50,
            BaseRetryDelayMilliseconds = 1,
            MaximumRetryDelayMilliseconds = 2,
            MaximumRetryAgeMilliseconds = 100,
            ShutdownDrainTimeoutMilliseconds = 25
        };
        await using var checkpoints =
            new CharacterCheckpointCoordinator(store, options);
        var run = checkpoints.RunAsync();
        await checkpoints.WaitUntilReadyAsync().WaitAsync(
            TimeSpan.FromSeconds(2));
        var queued = checkpoints.TryEnqueue(
            new CharacterPositionCheckpoint(
                1,
                1,
                new PlayerOwnershipFence(Guid.NewGuid(), 1),
                1,
                1,
                1,
                1));
        Check.True(queued.Accepted, "shutdown fixture queues one checkpoint");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var shutdown = new CancellationTokenSource();
        var completed = await CriticalTaskShutdown.CompleteAsync(
            checkpoints,
            shutdown,
            [run],
            options.ShutdownDrainTimeout);
        Check.True(
            !completed,
            "uncooperative checkpoint reaches the finite shutdown deadline");
        Check.True(
            shutdown.IsCancellationRequested,
            "checkpoint shutdown also cancels the host");

        release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void CheckSimulationLoopReadiness()
    {
        var grace = TimeSpan.FromSeconds(15);
        Check.True(
            ServerReadinessMonitor.IsSimulationLoopReady(
                new SimulationLoopRuntimeSnapshot(
                    SimulationLoopKind.ZodiacEnergyAccrual,
                    1,
                    TimeSpan.FromSeconds(40),
                    TimeSpan.FromSeconds(30)),
                grace),
            "slow healthy loop receives its expected-period allowance");
        Check.True(
            !ServerReadinessMonitor.IsSimulationLoopReady(
                new SimulationLoopRuntimeSnapshot(
                    SimulationLoopKind.MonsterWorld,
                    1,
                    TimeSpan.FromSeconds(46),
                    TimeSpan.FromSeconds(30)),
                grace),
            "stale simulation heartbeat removes readiness");
        Check.True(
            !ServerReadinessMonitor.IsSimulationLoopReady(
                new SimulationLoopRuntimeSnapshot(
                    SimulationLoopKind.MonsterWorld,
                    0,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1)),
                grace),
            "inactive required simulation loop removes readiness");
    }

    private static void CheckReadinessOptions()
    {
        var options = new ServerReadinessMonitorOptions();
        options.Validate();
        Check.Throws<InvalidDataException>(
            () => new ServerReadinessMonitorOptions
            {
                PollIntervalMilliseconds = 0
            }.Validate(),
            "zero readiness poll interval is rejected");
        Check.Throws<InvalidDataException>(
            () => new ServerReadinessMonitorOptions
            {
                MaximumWorkerHeartbeatAgeMilliseconds =
                    int.MaxValue
            }.Validate(),
            "unbounded readiness heartbeat threshold is rejected");
    }

    private static void CheckWorkerSnapshots()
    {
        PostgresCommandMetrics.UpdateBacklog(
            7,
            TimeSpan.FromSeconds(3));
        PostgresCommandMetrics.MarkOutboxStarted();
        var outbox = PostgresCommandMetrics.GetSnapshot();
        Check.True(
            outbox.State == OutboxDispatcherState.Running,
            "outbox runtime state is running");
        Check.Equal(7L, outbox.BacklogCount, "outbox backlog snapshot");
        Check.True(
            outbox.HeartbeatAge < TimeSpan.FromSeconds(1),
            "outbox heartbeat is fresh");

        var registry = new GameSessionRegistry();
        var progression =
            registry.GetDurableProgressionRetrySnapshot();
        Check.True(
            !progression.Enabled,
            "durable progression retry is explicitly disabled without PostgreSQL");
        Check.Equal(
            4_096,
            progression.Capacity,
            "durable progression retry has a fixed capacity");
        Check.Equal(
            0,
            progression.QueueDepth,
            "disabled progression retry has no queued value");
    }

    private static async Task CheckDrainAsync()
    {
        var state = new ServerOperationalState(
            ServerReadinessDependency.None);
        Check.True(state.TryMarkRunning(), "test server enters running state");
        var admission = new ConnectionAdmission(
            new ConnectionAdmissionOptions(4, 4, 4, 4));
        using var existing = Acquire(admission);
        using var shutdown = new CancellationTokenSource();
        var drain = new ServerDrainCoordinator(
            state,
            admission,
            shutdown,
            TimeSpan.FromMilliseconds(250));

        Check.True(
            drain.BeginDrain() == ManagementDrainResult.Accepted,
            "first drain request is accepted");
        Check.True(
            drain.BeginDrain() ==
                ManagementDrainResult.AlreadyDraining,
            "repeated drain request is idempotent");
        Check.True(
            state.GetSnapshot().ReadinessReason ==
                ServerReadinessReason.Draining,
            "drain removes readiness before shutdown");
        Check.True(
            !admission.TryAcquire(
                NetworkEndpointRole.Login,
                IPAddress.IPv6Loopback,
                out _,
                out var rejection) &&
            rejection == ConnectionAdmissionRejection.Draining,
            "drain rejects new sessions while retaining existing sessions");
        existing.Dispose();

        await drain.GetCompletion().WaitAsync(
            TimeSpan.FromSeconds(3));
        Check.True(
            shutdown.IsCancellationRequested,
            "bounded drain requests host shutdown");
    }

    private static ConnectionAdmissionLease Acquire(
        ConnectionAdmission admission)
    {
        Check.True(
            admission.TryAcquire(
                NetworkEndpointRole.Game,
                IPAddress.Loopback,
                out var lease,
                out _),
            "existing session is admitted before drain");
        return lease!;
    }

    private sealed class ShutdownCheckpointStore :
        ICharacterCheckpointStore
    {
        public Func<
            CharacterPositionCheckpoint,
            CancellationToken,
            Task<CharacterCheckpointWriteResult>>? PositionWrite
        { get; init; }

        public Task<CharacterCheckpointOwnership?> AcquireAsync(
            int accountId,
            int characterId,
            Guid ownerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterCheckpointOwnership?>(null);

        public Task<CharacterCheckpointWriteResult> WritePositionAsync(
            CharacterPositionCheckpoint checkpoint,
            CancellationToken cancellationToken = default) =>
            PositionWrite?.Invoke(checkpoint, cancellationToken) ??
            Task.FromResult(
                new CharacterCheckpointWriteResult(
                    CharacterCheckpointWriteStatus.Applied,
                    checkpoint.Revision));

        public Task<CharacterCheckpointWriteResult> WriteVitalsAsync(
            CharacterVitalsCheckpoint checkpoint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new CharacterCheckpointWriteResult(
                    CharacterCheckpointWriteStatus.Applied,
                    checkpoint.Revision));

        public Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
            int accountId,
            int characterId,
            PlayerOwnershipFence owner,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CharacterCheckpointReleaseStatus.Released);
    }
}
