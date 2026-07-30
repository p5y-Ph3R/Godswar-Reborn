using System.Net;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ManagementOptionsChecks
{
    public static async Task RunAsync()
    {
        var defaults = new ManagementOptions();
        Check.True(
            IPAddress.Loopback.Equals(defaults.Validate()),
            "management defaults bind exact IPv4 loopback");

        var ipv6 = new ManagementOptions
        {
            BindHost = "::1"
        };
        Check.True(
            IPAddress.IPv6Loopback.Equals(ipv6.Validate()),
            "management permits exact IPv6 loopback");

        foreach (var host in new[]
                 {
                     "localhost",
                     "127.0.0.2",
                     "0.0.0.0",
                     "10.0.0.1",
                     " ::1"
                 })
        {
            var invalid = new ManagementOptions { BindHost = host };
            Check.Throws<InvalidDataException>(
                () => invalid.Validate(),
                $"management rejects non-exact bind '{host}'");
        }

        var collision = new ManagementOptions { Port = 9090 };
        Check.Throws<InvalidDataException>(
            () => collision.Validate(5999, 9090),
            "management rejects an application TCP port collision");

        Reject(
            options => options.ListenBacklog = 0,
            "zero listener backlog");
        Reject(
            options => options.MaximumConcurrentRequests = 65,
            "excess request concurrency");
        Reject(
            options => options.MaximumHeaderBytes = 511,
            "undersized header budget");
        Reject(
            options => options.MaximumResponseBytes = 1024 * 1024 + 1,
            "excess response budget");
        Reject(
            options => options.RequestTimeoutMilliseconds = 49,
            "undersized request timeout");
        Reject(
            options => options.ResponseTimeoutMilliseconds = 5_001,
            "excess response timeout");
        CheckDisabledManagementIsolation();
        await CheckBoundedDrainTokenFileAsync();
    }

    private static Task CheckBoundedDrainTokenFileAsync()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"godswar-b13-{Guid.NewGuid():N}.token");
        try
        {
            var token = Enumerable.Repeat((byte)'T', 32).ToArray();
            File.WriteAllBytes(path, token);
            using var authenticator =
                ManagementDrainTokenFile.TryLoad(path);
            Check.True(
                authenticator?.Authenticate(token) == true,
                "bounded management token file loads exact secret");

            File.WriteAllBytes(path, new byte[259]);
            Check.Throws<InvalidDataException>(
                () => ManagementDrainTokenFile.TryLoad(path),
                "management token reader rejects bytes beyond its bound");
        }
        finally
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static void CheckDisabledManagementIsolation()
    {
        var disabled = new ServerOperationsOptions
        {
            Management = new ManagementOptions
            {
                Enabled = false,
                BindHost = "not-a-listener",
                Port = 0
            },
            DrainTokenFile = "unused-relative-token"
        };
        disabled.Validate(5999, 7000);

        disabled.Management.MaximumResponseBytes = 1024 * 1024 + 1;
        Check.Throws<InvalidDataException>(
            () => disabled.Validate(5999, 7000),
            "disabled management still validates observability resource bounds");
    }

    private static void Reject(
        Action<ManagementOptions> mutate,
        string description)
    {
        var options = new ManagementOptions();
        mutate(options);
        Check.Throws<InvalidDataException>(
            () => options.Validate(),
            description);
    }
}
