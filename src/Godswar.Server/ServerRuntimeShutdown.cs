using Godswar.Server.Game;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server;

internal static class ServerRuntimeShutdown
{
    public static void StartControlledHostShutdown(
        ControlledHostShutdownControl? control,
        CancellationTokenSource shutdown,
        ICollection<Task> auxiliaryTasks)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(auxiliaryTasks);
        if (control is null)
        {
            return;
        }

        var controlTask = control.RunAsync(shutdown.Token);
        auxiliaryTasks.Add(controlTask);
        _ = controlTask.ContinueWith(
            static (_, state) =>
                ((CancellationTokenSource)state!).Cancel(),
            shutdown,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static async Task<bool> TryDisposeWorldInstancesAsync(
        GameSessionRegistry registry,
        ServerObservabilityRuntime observability)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(observability);
        try
        {
            // Producers stop before bounded world-owner shutdown.
            await registry.DisposeAsync();
            return true;
        }
        catch
        {
            observability.RecordLifecycle(
                "world_instances",
                "shutdown_faulted",
                OperationalLogLevel.Critical);
            return false;
        }
    }

    public static void SetProcessOutcome(
        bool fatalRuntimeFailure,
        ServerObservabilityRuntime observability)
    {
        ArgumentNullException.ThrowIfNull(observability);
        if (fatalRuntimeFailure)
        {
            Environment.ExitCode = 4;
            return;
        }

        observability.RecordLifecycle("server", "stopped");
    }
}
