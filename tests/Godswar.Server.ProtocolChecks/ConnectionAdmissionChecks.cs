using System.Collections.Concurrent;
using System.Net;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static class ConnectionAdmissionChecks
{
    public static Task RunAsync()
    {
        CheckOptionsAndMetricTags();
        CheckInvalidInputs();
        CheckLimitRejections();
        CheckAddressNormalizationAndPrefixes();
        CheckAuthenticationAndIdempotentRelease();
        CheckDrainAdmission();
        CheckConcurrentAdmission();
        return Task.CompletedTask;
    }

    private static void CheckOptionsAndMetricTags()
    {
        var defaults = ConnectionAdmissionOptions.Default;
        Check.Equal(512, defaults.MaxActiveConnections, "default active connection limit");
        Check.Equal(128, defaults.MaxUnauthenticatedConnections, "default unauthenticated limit");
        Check.Equal(4, defaults.MaxUnauthenticatedConnectionsPerIp, "default per-IP limit");
        Check.Equal(32, defaults.MaxUnauthenticatedConnectionsPerPrefix, "default prefix limit");

        Check.Throws<ArgumentOutOfRangeException>(
            () => new ConnectionAdmission(defaults with { MaxActiveConnections = -1 }),
            "negative connection admission limits fail configuration validation");
        Check.Equal("login", NetworkEndpointRole.Login.ToMetricTag(), "login metric tag is finite");
        Check.Equal("game", NetworkEndpointRole.Game.ToMetricTag(), "game metric tag is finite");
        Check.Equal(
            "per_ip_limit",
            ConnectionAdmissionRejection.PerIpLimit.ToMetricTag(),
            "admission rejection metric tag is finite");
        Check.Equal(
            "draining",
            ConnectionAdmissionRejection.Draining.ToMetricTag(),
            "drain rejection metric tag is finite");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ((NetworkEndpointRole)byte.MaxValue).ToMetricTag(),
            "unknown role cannot create an attacker-controlled metric tag");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ((ConnectionAdmissionRejection)byte.MaxValue).ToMetricTag(),
            "unknown rejection cannot create an attacker-controlled metric tag");
    }

    private static void CheckDrainAdmission()
    {
        var admission = CreateAdmission();
        using var existing = Acquire(
            admission,
            IPAddress.Loopback,
            NetworkEndpointRole.Game);

        admission.BeginDrain();
        admission.BeginDrain();

        var snapshot = admission.GetSnapshot();
        Check.True(snapshot.IsDraining, "drain state is idempotently visible");
        Check.Equal(
            1,
            snapshot.ActiveConnections,
            "drain preserves existing active sessions");
        CheckRejected(
            admission,
            NetworkEndpointRole.Login,
            IPAddress.IPv6Loopback,
            ConnectionAdmissionRejection.Draining,
            "new admission while draining");
    }

    private static void CheckInvalidInputs()
    {
        var admission = CreateAdmission();

        CheckRejected(
            admission,
            NetworkEndpointRole.Login,
            null,
            ConnectionAdmissionRejection.InvalidRemoteAddress,
            "null remote address");
        CheckRejected(
            admission,
            NetworkEndpointRole.Login,
            IPAddress.Any,
            ConnectionAdmissionRejection.InvalidRemoteAddress,
            "unspecified IPv4 remote address");
        CheckRejected(
            admission,
            NetworkEndpointRole.Login,
            IPAddress.IPv6Any,
            ConnectionAdmissionRejection.InvalidRemoteAddress,
            "unspecified IPv6 remote address");
        CheckRejected(
            admission,
            (NetworkEndpointRole)99,
            IPAddress.Loopback,
            ConnectionAdmissionRejection.InvalidEndpointRole,
            "unknown endpoint role");

        Check.Equal(
            new ConnectionAdmissionSnapshot(),
            admission.GetSnapshot(),
            "invalid admission attempts do not reserve capacity");
    }

    private static void CheckLimitRejections()
    {
        using (var first = Acquire(
                   CreateAdmission(active: 1, unauthenticated: 1, perIp: 1, perPrefix: 1),
                   IPAddress.Loopback,
                   out var admission))
        {
            CheckRejected(
                admission,
                NetworkEndpointRole.Game,
                IPAddress.IPv6Loopback,
                ConnectionAdmissionRejection.ActiveLimit,
                "global active limit");
        }

        using (var first = Acquire(
                   CreateAdmission(active: 10, unauthenticated: 1, perIp: 1, perPrefix: 1),
                   IPAddress.Loopback,
                   out var admission))
        {
            CheckRejected(
                admission,
                NetworkEndpointRole.Game,
                IPAddress.IPv6Loopback,
                ConnectionAdmissionRejection.UnauthenticatedLimit,
                "global unauthenticated limit");
        }

        using (var first = Acquire(
                   CreateAdmission(
                       active: 10,
                       unauthenticated: 10,
                       perIp: 1,
                       perPrefix: 10),
                   IPAddress.Loopback,
                   out var admission))
        {
            CheckRejected(
                admission,
                NetworkEndpointRole.Game,
                IPAddress.Loopback,
                ConnectionAdmissionRejection.PerIpLimit,
                "per-IP unauthenticated limit");
        }

        var prefixAdmission = CreateAdmission(
            active: 10,
            unauthenticated: 10,
            perIp: 2,
            perPrefix: 2);
        using var prefixFirst = Acquire(prefixAdmission, IPAddress.Parse("198.51.100.1"));
        using var prefixSecond = Acquire(prefixAdmission, IPAddress.Parse("198.51.100.2"));
        CheckRejected(
            prefixAdmission,
            NetworkEndpointRole.Game,
            IPAddress.Parse("198.51.100.254"),
            ConnectionAdmissionRejection.PrefixLimit,
            "IPv4 /24 unauthenticated limit");
        using var neighboringPrefix = Acquire(prefixAdmission, IPAddress.Parse("198.51.101.1"));
    }

    private static void CheckAddressNormalizationAndPrefixes()
    {
        var mappedAdmission = CreateAdmission(perIp: 1);
        using var ipv4 = Acquire(mappedAdmission, IPAddress.Parse("192.0.2.7"));
        CheckRejected(
            mappedAdmission,
            NetworkEndpointRole.Game,
            IPAddress.Parse("::ffff:192.0.2.7"),
            ConnectionAdmissionRejection.PerIpLimit,
            "IPv4-mapped IPv6 shares the canonical IPv4 admission key");

        var ipv6Admission = CreateAdmission(perIp: 1, perPrefix: 1);
        using var first = Acquire(ipv6Admission, IPAddress.Parse("2001:db8:1234:5678::1"));
        CheckRejected(
            ipv6Admission,
            NetworkEndpointRole.Game,
            IPAddress.Parse("2001:db8:1234:5678:ffff::1"),
            ConnectionAdmissionRejection.PrefixLimit,
            "IPv6 /64 unauthenticated limit");
        using var neighboringPrefix = Acquire(
            ipv6Admission,
            IPAddress.Parse("2001:db8:1234:5679::1"));
    }

    private static void CheckAuthenticationAndIdempotentRelease()
    {
        var admission = CreateAdmission(
            active: 2,
            unauthenticated: 1,
            perIp: 1,
            perPrefix: 1);
        var login = Acquire(admission, IPAddress.Loopback, NetworkEndpointRole.Login);
        login.MarkAuthenticated();
        login.MarkAuthenticated();

        var game = Acquire(admission, IPAddress.Loopback, NetworkEndpointRole.Game);
        var snapshot = admission.GetSnapshot();
        Check.Equal(2, snapshot.ActiveConnections, "authentication retains active reservation");
        Check.Equal(1, snapshot.UnauthenticatedConnections, "authentication releases unauthenticated reservation");
        Check.Equal(1, snapshot.LoginActiveConnections, "snapshot separates active login role");
        Check.Equal(0, snapshot.LoginUnauthenticatedConnections, "authenticated login leaves unauth role count");
        Check.Equal(1, snapshot.GameUnauthenticatedConnections, "snapshot includes unauthenticated game role");

        game.MarkAuthenticated();
        snapshot = admission.GetSnapshot();
        Check.Equal(0, snapshot.UnauthenticatedConnections, "all authenticated leases leave unauth global count");
        Check.Equal(0, snapshot.TrackedUnauthenticatedIpAddresses, "empty IP counter entries are removed");
        Check.Equal(0, snapshot.TrackedUnauthenticatedPrefixes, "empty prefix counter entries are removed");

        Parallel.Invoke(login.Dispose, login.Dispose, game.Dispose, game.Dispose);
        login.MarkAuthenticated();
        snapshot = admission.GetSnapshot();
        Check.Equal(0, snapshot.ActiveConnections, "concurrent repeated release is idempotent");
        Check.Equal(0, snapshot.UnauthenticatedConnections, "released lease cannot re-enter unauthenticated state");
    }

    private static void CheckConcurrentAdmission()
    {
        const int limit = 8;
        var admission = CreateAdmission(
            active: limit,
            unauthenticated: limit,
            perIp: limit,
            perPrefix: limit);
        var leases = new ConcurrentBag<ConnectionAdmissionLease>();

        Parallel.For(
            0,
            256,
            ignored =>
            {
                if (admission.TryAcquire(
                        NetworkEndpointRole.Game,
                        IPAddress.Loopback,
                        out var lease,
                        out var rejection))
                {
                    leases.Add(lease);
                }
            });

        Check.Equal(limit, leases.Count, "concurrent acquisition never overshoots configured limits");
        Check.Equal(limit, admission.GetSnapshot().ActiveConnections, "concurrent snapshot matches acquired leases");
        Parallel.ForEach(leases, lease => Parallel.Invoke(lease.Dispose, lease.Dispose));
        Check.Equal(0, admission.GetSnapshot().ActiveConnections, "concurrent lease release returns all capacity");
    }

    private static ConnectionAdmission CreateAdmission(
        int active = 32,
        int unauthenticated = 32,
        int perIp = 32,
        int perPrefix = 32)
    {
        return new ConnectionAdmission(
            new ConnectionAdmissionOptions(active, unauthenticated, perIp, perPrefix));
    }

    private static ConnectionAdmissionLease Acquire(
        ConnectionAdmission admission,
        IPAddress address,
        NetworkEndpointRole role = NetworkEndpointRole.Login)
    {
        Check.True(
            admission.TryAcquire(role, address, out var lease, out var rejection),
            $"connection admission succeeds instead of {rejection}");
        return lease!;
    }

    private static ConnectionAdmissionLease Acquire(
        ConnectionAdmission admission,
        IPAddress address,
        out ConnectionAdmission capturedAdmission)
    {
        capturedAdmission = admission;
        return Acquire(admission, address);
    }

    private static void CheckRejected(
        ConnectionAdmission admission,
        NetworkEndpointRole role,
        IPAddress? address,
        ConnectionAdmissionRejection expected,
        string description)
    {
        Check.True(
            !admission.TryAcquire(role, address, out var lease, out var rejection),
            $"{description} is rejected");
        Check.True(lease is null, $"{description} does not return a lease");
        Check.True(rejection == expected, $"{description} reports a finite reason");
    }
}
