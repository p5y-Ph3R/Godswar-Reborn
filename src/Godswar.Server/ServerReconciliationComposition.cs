using Godswar.Server.Infrastructure;
using Godswar.Server.Operations;

namespace Godswar.Server;

internal static class ServerReconciliationComposition
{
    public static void StartWorkerIfEnabled(
        CriticalTaskCollection tasks,
        PostgresApplicationDataRuntime? runtime)
    {
        if (runtime?.ReconciliationEnabled != true)
        {
            return;
        }

        tasks.Start(
            CriticalTaskKind.Reconciliation,
            runtime.RunReconciliationAsync);
    }
}
