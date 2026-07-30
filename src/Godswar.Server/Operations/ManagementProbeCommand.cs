using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Godswar.Server.Operations;

internal enum ManagementProbeKind : byte
{
    Live = 1,
    Ready = 2
}

/// <summary>
/// In-image probe client for Docker healthchecks. It can connect only to the
/// configured exact loopback address and reads a bounded HTTP response header.
/// </summary>
internal static class ManagementProbeCommand
{
    internal const string Mode = "--management-probe";
    internal const int MaximumResponseHeaderBytes = 4 * 1024;
    private static readonly TimeSpan DefaultTimeout =
        TimeSpan.FromSeconds(2);

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
        if (args.Length != 3 ||
            !TryParseKind(args[1], out var kind) ||
            !int.TryParse(args[2], out var port) ||
            port is < 1 or > ushort.MaxValue)
        {
            return true;
        }

        try
        {
            var configuredHost =
                Environment.GetEnvironmentVariable(
                    "GODSWAR_MANAGEMENT_BIND_HOST") ??
                ManagementOptions.DefaultBindHost;
            if (!ManagementOptions.TryParseExactLoopback(
                    configuredHost,
                    out var address))
            {
                return true;
            }
            var healthy = await ProbeAsync(
                kind,
                address,
                port,
                DefaultTimeout,
                cancellationToken);
            Environment.ExitCode = healthy ? 0 : 1;
        }
        catch
        {
            Environment.ExitCode = 1;
        }

        return true;
    }

    internal static async Task<bool> ProbeAsync(
        ManagementProbeKind kind,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => await ProbeAsync(
            kind,
            IPAddress.Loopback,
            port,
            timeout,
            cancellationToken);

    internal static async Task<bool> ProbeAsync(
        ManagementProbeKind kind,
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ArgumentNullException.ThrowIfNull(address);
        if (!IPAddress.IsLoopback(address))
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        if (timeout < TimeSpan.FromMilliseconds(50) ||
            timeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(timeout);
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(
            address,
            port,
            deadline.Token);
        using var stream = client.GetStream();

        var path = kind == ManagementProbeKind.Live
            ? "/livez"
            : "/readyz";
        var host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]:{port}"
            : $"{address}:{port}";
        var request = Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: {host}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(request, deadline.Token);
        await stream.FlushAsync(deadline.Token);

        var buffer = ArrayPool<byte>.Shared.Rent(
            MaximumResponseHeaderBytes + 1);
        try
        {
            var count = 0;
            while (count <= MaximumResponseHeaderBytes)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(
                        count,
                        MaximumResponseHeaderBytes + 1 - count),
                    deadline.Token);
                if (read == 0)
                {
                    return false;
                }

                count += read;
                var header = buffer.AsSpan(0, count);
                var end = header.IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    var lineEnd = header.IndexOf("\r\n"u8);
                    return lineEnd > 0 &&
                        header[..lineEnd].SequenceEqual(
                            "HTTP/1.1 200 OK"u8);
                }
                if (count > MaximumResponseHeaderBytes)
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        return false;
    }

    private static bool TryParseKind(
        string value,
        out ManagementProbeKind kind)
    {
        if (string.Equals(value, "live", StringComparison.Ordinal))
        {
            kind = ManagementProbeKind.Live;
            return true;
        }
        if (string.Equals(value, "ready", StringComparison.Ordinal))
        {
            kind = ManagementProbeKind.Ready;
            return true;
        }

        kind = default;
        return false;
    }
}
