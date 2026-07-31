using Godswar.Server.Application.Gateway;
using Godswar.Server.Operations;

namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Opt-in local gateway process mode for the unchanged legacy client.
/// Client-facing raw TCP remains loopback-only; every worker hop is mTLS.
/// </summary>
internal static class SemanticGatewayCommand
{
    internal const string Mode = "--semantic-gateway";

    public static async Task<bool> TryRunAsync(
        string[] args,
        Func<
            ServerOptions,
            CancellationToken,
            ValueTask<ISemanticGatewayDataSession>> openDataSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(openDataSession);
        if (args.Length == 0 ||
            !string.Equals(args[0], Mode, StringComparison.Ordinal))
        {
            return false;
        }

        Environment.ExitCode = 2;
        if (args.Length != 3 ||
            string.IsNullOrWhiteSpace(args[1]) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            Console.Error.WriteLine(
                "[semantic-gateway] expected --semantic-gateway " +
                "<serverOptionsPath> <gatewayConfigPath>");
            return true;
        }

        using var shutdown =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        SemanticGatewayHost? host = null;
        void BeginDrain()
        {
            host?.BeginDrain();
            try
            {
                shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            BeginDrain();
        };
        Console.CancelKeyPress += cancelHandler;
        using var processSignals =
            ServerProcessSignalRegistration.Install(BeginDrain);
        try
        {
            var options = ServerOptions.Load(args[1]);
            ValidateServerOptions(options);
            using var configuration =
                await SemanticGatewayRuntimeOptions.LoadAsync(
                    args[2],
                    shutdown.Token);
            await using var data = await openDataSession(
                options,
                shutdown.Token);
            host = new SemanticGatewayHost(
                configuration,
                data);
            await using (host)
            {
                var run = host.RunAsync(shutdown.Token);
                var endpoints = await host.WaitUntilStartedAsync(
                    shutdown.Token);
                Console.WriteLine(
                    "[semantic-gateway] ready " +
                    $"login={endpoints.Login} game={endpoints.Game}");
                Environment.ExitCode = 0;
                await run;
            }
        }
        catch (OperationCanceledException)
            when (shutdown.IsCancellationRequested)
        {
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
            when (ex is InvalidDataException or
                ServerStartupConfigurationException)
        {
            Console.Error.WriteLine(
                "[semantic-gateway] configuration rejected: " +
                ex.Message);
            Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[semantic-gateway] runtime failed: " +
                ex.GetType().Name);
            Environment.ExitCode = 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        return true;
    }

    private static void ValidateServerOptions(ServerOptions options)
    {
        var profile = ServerRuntimeProfilePolicy.Validate(options);
        if (profile.RuntimeProfile !=
                ServerRuntimeProfileKind.LocalDevelopment ||
            profile.Transport != ServerListenerTransport.RawTcp ||
            !profile.AllowsLegacyAuthentication)
        {
            throw new InvalidDataException(
                "The unchanged-client semantic gateway requires the " +
                "explicit LocalDevelopment legacy-raw profile.");
        }
        if (options.Backhaul.Enabled || options.Secure.Enabled)
        {
            throw new InvalidDataException(
                "Gateway server options cannot also enable a worker " +
                "backhaul or secure public listener.");
        }
    }
}
