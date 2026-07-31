using Godswar.Server.Game;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server;

internal static class ServerRuntimeShutdown
{
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
