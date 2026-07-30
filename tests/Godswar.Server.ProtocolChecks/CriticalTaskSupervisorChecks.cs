using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class CriticalTaskSupervisorChecks
{
    public static async Task RunAsync()
    {
        await CheckCancellationIsNormalAsync();
        await CheckUnexpectedCompletionIsFatalAsync();
        await CheckFaultIsFatalAsync();
    }

    private static async Task CheckCancellationIsNormalAsync()
    {
        var state = State();
        using var stop = new CancellationTokenSource();
        var shutdownRequests = 0;
        var entered = NewSignal();
        var supervisor = new CriticalTaskSupervisor(
            state,
            () => Interlocked.Increment(ref shutdownRequests));
        var task = supervisor.RunAsync(
            CriticalTaskKind.LoginListener,
            async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            },
            stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        supervisor.SealRegistrations();

        Check.True(
            state.GetSnapshot().ReadyDependencies.HasFlag(
                ServerReadinessDependency.CriticalTasks),
            "sealed running task publishes critical-task readiness");
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(
            0,
            shutdownRequests,
            "host cancellation is not reported as a task fault");
        Check.True(
            supervisor.GetSnapshot().Tasks.Single().State ==
                CriticalTaskState.Stopped,
            "cancelled task records finite stopped state");
    }

    private static async Task CheckUnexpectedCompletionIsFatalAsync()
    {
        var state = State();
        var release = NewSignal();
        var entered = NewSignal();
        var shutdownRequests = 0;
        var supervisor = new CriticalTaskSupervisor(
            state,
            () => Interlocked.Increment(ref shutdownRequests));
        var task = supervisor.RunAsync(
            CriticalTaskKind.OutboxDispatcher,
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            },
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        supervisor.SealRegistrations();
        release.TrySetResult();

        await CheckThrowsAsync<CriticalTaskStoppedException>(
            task,
            "unexpected successful completion is fatal");
        Check.Equal(
            1,
            shutdownRequests,
            "unexpected completion requests shutdown exactly once");
        Check.True(
            !state.GetSnapshot().IsLive,
            "unexpected completion removes liveness");
        Check.True(
            supervisor.GetSnapshot().FaultedTask ==
                CriticalTaskKind.OutboxDispatcher,
            "supervisor identifies only the finite task kind");
    }

    private static async Task CheckFaultIsFatalAsync()
    {
        var state = State();
        var release = NewSignal();
        var entered = NewSignal();
        var shutdownRequests = 0;
        var supervisor = new CriticalTaskSupervisor(
            state,
            () => Interlocked.Increment(ref shutdownRequests));
        var task = supervisor.RunAsync(
            CriticalTaskKind.PostgresReadiness,
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                throw new IOException("fixture");
            },
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        supervisor.SealRegistrations();
        release.TrySetResult();

        await CheckThrowsAsync<IOException>(
            task,
            "task exception is preserved");
        Check.Equal(
            1,
            shutdownRequests,
            "task fault requests shutdown exactly once");
        Check.True(
            supervisor.GetSnapshot().Tasks.Single().State ==
                CriticalTaskState.Faulted,
            "task fault records finite faulted state");
    }

    private static ServerOperationalState State()
    {
        var state = new ServerOperationalState(
            ServerReadinessDependency.CriticalTasks);
        state.TryMarkRunning();
        return state;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task CheckThrowsAsync<TException>(
        Task task,
        string description)
        where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }
}
