using Godswar.Server.Operations;

namespace Godswar.Server;

internal static class ServerRuntimeBootstrap
{
    public static bool TryLoadOptions(
        string optionsPath,
        out ServerOptions options,
        out ValidatedServerRuntimeProfile runtimeProfile)
    {
        try
        {
            options = ServerOptions.Load(optionsPath);
            runtimeProfile =
                ServerRuntimeProfilePolicy.Validate(options);
            return true;
        }
        catch (ServerStartupConfigurationException error)
        {
            Reject(
                ServerRuntimeProfilePolicy.RejectionCode(
                    error.Reason));
        }
        catch (Exception)
        {
            Reject("invalid_configuration");
        }

        options = default!;
        runtimeProfile = default!;
        return false;
    }

    private static void Reject(string reason)
    {
        ServerProfileMetrics.RecordStartupRejection(reason);
        Console.Error.WriteLine(
            $"[startup] rejected reason={reason}");
        Environment.ExitCode = 2;
    }
}
