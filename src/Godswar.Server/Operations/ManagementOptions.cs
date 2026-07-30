using System.Net;

namespace Godswar.Server.Operations;

/// <summary>
/// Finite resource and deadline policy for the process-local management
/// listener. The first management increment is deliberately loopback-only.
/// </summary>
internal sealed class ManagementOptions
{
    public const string DefaultBindHost = "127.0.0.1";
    public const int DefaultPort = 9090;

    public bool Enabled { get; set; } = true;

    public string BindHost { get; set; } = DefaultBindHost;

    public int Port { get; set; } = DefaultPort;

    public int ListenBacklog { get; set; } = 16;

    public int MaximumConcurrentRequests { get; set; } = 8;

    public int MaximumHeaderBytes { get; set; } = 4 * 1024;

    public int MaximumResponseBytes { get; set; } = 64 * 1024;

    public int RequestTimeoutMilliseconds { get; set; } = 1_000;

    public int ResponseTimeoutMilliseconds { get; set; } = 1_000;

    public TimeSpan RequestTimeout =>
        TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds);

    public TimeSpan ResponseTimeout =>
        TimeSpan.FromMilliseconds(ResponseTimeoutMilliseconds);

    public IPAddress Validate(params int[] reservedTcpPorts)
    {
        ValidateResourceBounds();
        if (!TryParseExactLoopback(BindHost, out var address))
        {
            throw new InvalidDataException(
                "Management.BindHost must be exactly '127.0.0.1' or '::1'.");
        }
        if (Port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                "Management.Port must be between 1 and 65535.");
        }
        if ((reservedTcpPorts ?? [])
            .Where(static port => port is >= 1 and <= ushort.MaxValue)
            .Contains(Port))
        {
            throw new InvalidDataException(
                "Management.Port must not collide with an application TCP listener.");
        }

        return address;
    }

    internal void ValidateResourceBounds()
    {
        RequireRange(
            ListenBacklog,
            minimum: 1,
            maximum: 128,
            nameof(ListenBacklog));
        RequireRange(
            MaximumConcurrentRequests,
            minimum: 1,
            maximum: 64,
            nameof(MaximumConcurrentRequests));
        RequireRange(
            MaximumHeaderBytes,
            minimum: 512,
            maximum: 16 * 1024,
            nameof(MaximumHeaderBytes));
        RequireRange(
            MaximumResponseBytes,
            minimum: 1_024,
            maximum: 1024 * 1024,
            nameof(MaximumResponseBytes));
        RequireRange(
            RequestTimeoutMilliseconds,
            minimum: 50,
            maximum: 5_000,
            nameof(RequestTimeoutMilliseconds));
        RequireRange(
            ResponseTimeoutMilliseconds,
            minimum: 50,
            maximum: 5_000,
            nameof(ResponseTimeoutMilliseconds));
    }

    internal static bool TryParseExactLoopback(
        string? value,
        out IPAddress address)
    {
        if (string.Equals(
                value,
                IPAddress.Loopback.ToString(),
                StringComparison.Ordinal))
        {
            address = IPAddress.Loopback;
            return true;
        }
        if (string.Equals(
                value,
                IPAddress.IPv6Loopback.ToString(),
                StringComparison.Ordinal))
        {
            address = IPAddress.IPv6Loopback;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private static void RequireRange(
        int value,
        int minimum,
        int maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"Management.{name} must be between {minimum} and {maximum}.");
        }
    }
}
