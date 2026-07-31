using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ServerOperationalStateChecks
{
    public static Task RunAsync()
    {
        CheckRedisCoordinationReadinessReason();
        const ServerReadinessDependency required =
            ServerReadinessDependency.ListenerProfile |
            ServerReadinessDependency.Database |
            ServerReadinessDependency.PersistenceWorkers |
            ServerReadinessDependency.CriticalTasks;
        var state = new ServerOperationalState(required);

        AssertSnapshot(
            state.GetSnapshot(),
            live: true,
            ready: false,
            ServerReadinessReason.Starting,
            "initial startup state");
        Check.True(state.TryMarkRunning(), "startup enters running once");
        AssertSnapshot(
            state.GetSnapshot(),
            live: true,
            ready: false,
            ServerReadinessReason.ListenerProfileNotReady,
            "listener is first finite missing dependency");

        state.SetDependency(
            ServerReadinessDependency.ListenerProfile,
            ready: true);
        state.SetDependency(
            ServerReadinessDependency.Database,
            ready: true);
        AssertSnapshot(
            state.GetSnapshot(),
            live: true,
            ready: false,
            ServerReadinessReason.PersistenceWorkerNotReady,
            "persistence workers have a distinct readiness reason");

        state.SetDependency(
            ServerReadinessDependency.PersistenceWorkers,
            ready: true);
        state.SetDependency(
            ServerReadinessDependency.CriticalTasks,
            ready: true);
        var ready = state.GetSnapshot();
        AssertSnapshot(
            ready,
            live: true,
            ready: true,
            ServerReadinessReason.None,
            "all required cached dependencies are ready");

        state.SetDependency(
            ServerReadinessDependency.CriticalTasks,
            ready: true);
        Check.Equal(
            ready.Version,
            state.GetSnapshot().Version,
            "idempotent dependency publication does not churn version");

        Check.True(state.TryBeginDrain(), "running state begins drain");
        Check.True(state.TryBeginDrain(), "drain transition is idempotent");
        AssertSnapshot(
            state.GetSnapshot(),
            live: true,
            ready: false,
            ServerReadinessReason.Draining,
            "drain removes readiness but preserves liveness");

        state.MarkCriticalTaskFaulted();
        AssertSnapshot(
            state.GetSnapshot(),
            live: false,
            ready: false,
            ServerReadinessReason.CriticalTaskFaulted,
            "critical task fault removes liveness");
        state.MarkStopped();
        AssertSnapshot(
            state.GetSnapshot(),
            live: false,
            ready: false,
            ServerReadinessReason.Stopped,
            "stopped host remains not live");

        Check.Throws<ArgumentOutOfRangeException>(
            () => state.SetDependency(
                ServerReadinessDependency.Database |
                ServerReadinessDependency.CriticalTasks,
                ready: true),
            "one setter cannot ambiguously publish multiple dependencies");
        return Task.CompletedTask;
    }

    private static void CheckRedisCoordinationReadinessReason()
    {
        const ServerReadinessDependency required =
            ServerReadinessDependency.Database |
            ServerReadinessDependency.RedisCoordination;
        var state = new ServerOperationalState(required);
        Check.True(
            state.TryMarkRunning(),
            "coordination readiness fixture enters running");
        state.SetDependency(
            ServerReadinessDependency.Database,
            ready: true);
        AssertSnapshot(
            state.GetSnapshot(),
            live: true,
            ready: false,
            ServerReadinessReason.RedisCoordinationNotReady,
            "unavailable required Redis removes readiness");
        state.SetDependency(
            ServerReadinessDependency.RedisCoordination,
            ready: true);
        AssertSnapshot(
            state.GetSnapshot(),
            live: true,
            ready: true,
            ServerReadinessReason.None,
            "cached healthy Redis coordination restores readiness");
        state.SetDependency(
            ServerReadinessDependency.RedisCoordination,
            ready: false);
        AssertSnapshot(
            state.GetSnapshot(),
            live: true,
            ready: false,
            ServerReadinessReason.RedisCoordinationNotReady,
            "lost coordination readiness fails closed");
    }

    private static void AssertSnapshot(
        ServerOperationalSnapshot snapshot,
        bool live,
        bool ready,
        ServerReadinessReason reason,
        string description)
    {
        Check.True(snapshot.IsLive == live, $"{description} liveness");
        Check.True(snapshot.IsReady == ready, $"{description} readiness");
        Check.True(
            snapshot.ReadinessReason == reason,
            $"{description} reason");
    }
}
