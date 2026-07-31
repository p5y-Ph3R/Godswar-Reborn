using Godswar.Server.Operations;

namespace Godswar.Server.Networking.RelayGateway;

/// <summary>
/// Opt-in process mode. Program composition can call this before normal
/// ServerOptions loading; non-relay argument lists are left untouched.
/// </summary>
internal static class RelayGatewayCommand
{
    internal const string Mode = "--relay-gateway";

    public static async Task<bool> TryRunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 ||
            !string.Equals(args[0], Mode, StringComparison.Ordinal))
        {
            return false;
        }

        Environment.ExitCode = 2;
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine(
                "[relay-gateway] expected --relay-gateway <configPath>");
            return true;
        }

        using var shutdown =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            try
            {
                shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        };
        Console.CancelKeyPress += cancelHandler;
        using var processSignals =
            ServerProcessSignalRegistration.Install(
                () =>
                {
                    try
                    {
                        shutdown.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
        try
        {
            var configuration = await RelayGatewayOptions.LoadAsync(
                args[1],
                shutdown.Token);
            await using var gateway =
                new RelayGatewayServer(configuration);
            var run = gateway.RunAsync(shutdown.Token);
            var endpoints = await gateway.WaitUntilStartedAsync(
                shutdown.Token);
            Console.WriteLine(
                "[relay-gateway] ready " +
                $"login={endpoints.Login} game={endpoints.Game}");
            Environment.ExitCode = 0;
            await run;
            Console.WriteLine("[relay-gateway] stopped");
        }
        catch (OperationCanceledException)
            when (shutdown.IsCancellationRequested)
        {
            Environment.ExitCode = 0;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(
                $"[relay-gateway] configuration rejected: {ex.Message}");
            Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[relay-gateway] runtime failed: {ex.GetType().Name}");
            Environment.ExitCode = 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        return true;
    }
}
