using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server;

internal static class ServerManagementTokenComposition
{
    public static bool TryLoad(
        ServerOptions options,
        ServerObservabilityRuntime observability,
        out ManagementTokenAuthenticator? token)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(observability);
        token = null;
        try
        {
            if (options.Operations.Management.Enabled)
            {
                token = ManagementDrainTokenFile.TryLoad(
                    options.Operations.DrainTokenFile);
            }

            return true;
        }
        catch
        {
            observability.RecordLifecycle(
                "management",
                "configuration_rejected",
                OperationalLogLevel.Error);
            Environment.ExitCode = 2;
            return false;
        }
    }
}
