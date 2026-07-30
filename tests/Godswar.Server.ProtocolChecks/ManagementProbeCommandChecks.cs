using System.Net;
using System.Net.Sockets;
using System.Text;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ManagementProbeCommandChecks
{
    public static async Task RunAsync()
    {
        Check.True(
            !await ManagementProbeCommand.TryRunAsync([]),
            "unrelated invocation is not consumed");

        var previousExitCode = Environment.ExitCode;
        try
        {
            Check.True(
                await ManagementProbeCommand.TryRunAsync(
                    [ManagementProbeCommand.Mode, "unknown", "9090"]),
                "probe mode consumes malformed invocation");
            Check.Equal(
                2,
                Environment.ExitCode,
                "malformed probe invocation has usage exit code");

            var success = StartOneResponseServer(200);
            Check.True(
                await ManagementProbeCommand.ProbeAsync(
                    ManagementProbeKind.Ready,
                    success.Port,
                    TimeSpan.FromSeconds(2)),
                "probe accepts exact HTTP 200 response");
            await success.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var unavailable = StartOneResponseServer(503);
            Check.True(
                !await ManagementProbeCommand.ProbeAsync(
                    ManagementProbeKind.Live,
                    unavailable.Port,
                    TimeSpan.FromSeconds(2)),
                "probe rejects non-200 response");
            await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var commandServer = StartOneResponseServer(200);
            Check.True(
                await ManagementProbeCommand.TryRunAsync(
                [
                    ManagementProbeCommand.Mode,
                    "ready",
                    commandServer.Port.ToString()
                ]),
                "valid probe command is consumed");
            Check.Equal(
                0,
                Environment.ExitCode,
                "successful probe command has zero exit code");
            await commandServer.Task.WaitAsync(TimeSpan.FromSeconds(2));

            if (Socket.OSSupportsIPv6)
            {
                await CheckIpv6CommandAsync();
            }
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    private static async Task CheckIpv6CommandAsync()
    {
        const string variable = "GODSWAR_MANAGEMENT_BIND_HOST";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "::1");
            var server = StartOneResponseServer(
                200,
                IPAddress.IPv6Loopback);
            Check.True(
                await ManagementProbeCommand.TryRunAsync(
                [
                    ManagementProbeCommand.Mode,
                    "ready",
                    server.Port.ToString()
                ]),
                "IPv6 probe command is consumed");
            Check.Equal(
                0,
                Environment.ExitCode,
                "configured IPv6 loopback probe succeeds");
            await server.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static OneResponseServer StartOneResponseServer(
        int statusCode,
        IPAddress? address = null)
    {
        var listener = new TcpListener(
            address ?? IPAddress.Loopback,
            0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            try
            {
                using var timeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var client = await listener.AcceptTcpClientAsync(
                    timeout.Token);
                using var stream = client.GetStream();
                var request = new byte[512];
                _ = await stream.ReadAsync(request, timeout.Token);
                var reason = statusCode == 200
                    ? "OK"
                    : "Service Unavailable";
                var response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {statusCode} {reason}\r\n" +
                    "Content-Length: 0\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            finally
            {
                listener.Stop();
            }
        });
        return new OneResponseServer(port, task);
    }

    private readonly record struct OneResponseServer(
        int Port,
        Task Task);
}
