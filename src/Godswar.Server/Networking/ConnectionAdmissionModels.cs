using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Godswar.Server.Networking;

internal enum NetworkEndpointRole : byte
{
    Login = 1,
    Game = 2,
}

internal enum ConnectionAdmissionRejection : byte
{
    None = 0,
    ActiveLimit = 1,
    UnauthenticatedLimit = 2,
    PerIpLimit = 3,
    PrefixLimit = 4,
    InvalidRemoteAddress = 5,
    InvalidEndpointRole = 6,
}

internal readonly record struct ConnectionAdmissionOptions(
    int MaxActiveConnections,
    int MaxUnauthenticatedConnections,
    int MaxUnauthenticatedConnectionsPerIp,
    int MaxUnauthenticatedConnectionsPerPrefix)
{
    public static ConnectionAdmissionOptions Default { get; } = new(
        MaxActiveConnections: 512,
        MaxUnauthenticatedConnections: 128,
        MaxUnauthenticatedConnectionsPerIp: 4,
        MaxUnauthenticatedConnectionsPerPrefix: 32);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxActiveConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxUnauthenticatedConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxUnauthenticatedConnectionsPerIp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxUnauthenticatedConnectionsPerPrefix);

        if (MaxUnauthenticatedConnections > MaxActiveConnections
            || MaxUnauthenticatedConnectionsPerIp > MaxUnauthenticatedConnections
            || MaxUnauthenticatedConnectionsPerPrefix
                < MaxUnauthenticatedConnectionsPerIp
            || MaxUnauthenticatedConnectionsPerPrefix
                > MaxUnauthenticatedConnections)
        {
            throw new ArgumentException(
                "Connection admission limits must satisfy per-IP <= prefix <= unauthenticated <= active.");
        }
    }
}

internal readonly record struct ConnectionAdmissionSnapshot(
    int ActiveConnections,
    int UnauthenticatedConnections,
    int LoginActiveConnections,
    int LoginUnauthenticatedConnections,
    int GameActiveConnections,
    int GameUnauthenticatedConnections,
    int TrackedUnauthenticatedIpAddresses,
    int TrackedUnauthenticatedPrefixes);

internal interface IConnectionAdmission
{
    bool TryAcquire(
        NetworkEndpointRole role,
        IPAddress? remoteAddress,
        [NotNullWhen(true)] out ConnectionAdmissionLease? lease,
        out ConnectionAdmissionRejection rejection);

    ConnectionAdmissionSnapshot GetSnapshot();
}

internal static class ConnectionAdmissionMetricTags
{
    public static string ToMetricTag(this NetworkEndpointRole role)
    {
        return role switch
        {
            NetworkEndpointRole.Login => "login",
            NetworkEndpointRole.Game => "game",
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unknown network endpoint role."),
        };
    }

    public static string ToMetricTag(this ConnectionAdmissionRejection rejection)
    {
        return rejection switch
        {
            ConnectionAdmissionRejection.None => "none",
            ConnectionAdmissionRejection.ActiveLimit => "active_limit",
            ConnectionAdmissionRejection.UnauthenticatedLimit => "unauthenticated_limit",
            ConnectionAdmissionRejection.PerIpLimit => "per_ip_limit",
            ConnectionAdmissionRejection.PrefixLimit => "prefix_limit",
            ConnectionAdmissionRejection.InvalidRemoteAddress => "invalid_remote_address",
            ConnectionAdmissionRejection.InvalidEndpointRole => "invalid_endpoint_role",
            _ => throw new ArgumentOutOfRangeException(
                nameof(rejection),
                rejection,
                "Unknown connection admission rejection."),
        };
    }
}
