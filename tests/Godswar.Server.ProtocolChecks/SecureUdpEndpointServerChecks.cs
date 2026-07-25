using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpEndpointServerChecks
{
    private static readonly IPEndPoint ReceiveTemplate =
        new(IPAddress.Any, 0);

    public static async Task RunAsync()
    {
        await CheckLoopbackLifecycleAndBindingAsync();
        CheckRateLimiterBounds();
        CheckUnknownProtectedCandidateAdmission();
    }

    private static async Task CheckLoopbackLifecycleAndBindingAsync()
    {
        var connectionId = Enumerable.Range(1, 16)
            .Select(static value => checked((byte)value))
            .ToArray();
        var context = new SecureConnectionContext(
            SecureEndpointRole.Game,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            connectionId,
            Enumerable.Repeat((byte)0x31, 16).ToArray(),
            Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256));
        var principal = new SecureBoundGamePrincipal(
            7,
            "test2",
            SecureGamePermissions.EnterWorld,
            Guid.Parse("11111111-2222-3333-4444-555555555555"));
        using var authority = new SecureUdpSessionAuthority(
            capacity: 4,
            pendingTtl: TimeSpan.FromSeconds(30));
        var registration = authority.Register(context, principal);
        Check.True(
            registration.IsRegistered,
            "UDP listener authority registration");
        using var lease = registration.Lease!;
        var registeredId = new byte[16];
        var proofKey = new byte[32];
        Check.True(
            lease.TryCopyGrantMaterial(
                registeredId,
                proofKey,
                out _) &&
            registeredId.SequenceEqual(connectionId),
            "UDP listener proof material");

        var policy = new SecureUdpCookiePolicy(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(60));
        using var cookies = new SecureUdpCookieProtector(
            policy,
            serverId: 100,
            udpPort: 7_444,
            audience: "reborn-game");
        using var addressValidation =
            new SecureUdpAddressValidation(1_200, cookies);
        var coordinator = new SecureUdpBindingCoordinator(
            addressValidation,
            authority);
        var limiter = new SecureUdpRateLimiter(
            globalLimit: 1_000,
            prefixLimit: 1_000,
            prefixCapacity: 8);
        var server = new SecureUdpEndpointServer(
            "127.0.0.1",
            port: 0,
            maximumDatagramBytes: 1_200,
            coordinator,
            limiter);
        using var lifetime = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var runTask = server.RunAsync(lifetime.Token);
        var endpoint = await server.WaitUntilStartedAsync(
            lifetime.Token);
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var nonce = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        var hello = new byte[128];
        Check.True(
            SecureUdpAddressValidation.TryEncodeClientHello(
                connectionId,
                nonce,
                hello,
                out var helloBytes) &&
            helloBytes == hello.Length,
            "UDP listener hello fixture");
        foreach (var malformedLength in new[]
        {
            0,
            1,
            127,
            129,
            1_200,
            1_201
        })
        {
            await client.SendToAsync(
                new byte[malformedLength],
                SocketFlags.None,
                endpoint,
                lifetime.Token);
            await client.SendToAsync(
                hello,
                SocketFlags.None,
                endpoint,
                lifetime.Token);
            var challenge = await ReceiveExactChallengeAsync(
                client,
                lifetime.Token);
            Check.True(
                SecureUdpBindingCodec.TryDecode(
                    challenge,
                    out var decoded) &&
                decoded.Type ==
                    SecureUdpBindingType.ServerChallenge,
                $"UDP loop survives malformed length {malformedLength}");
        }

        await client.SendToAsync(
            hello,
            SocketFlags.None,
            endpoint,
            lifetime.Token);
        var bindingChallenge = await ReceiveExactChallengeAsync(
            client,
            lifetime.Token);
        var proof = new byte[128];
        Check.True(
            SecureUdpAddressValidation
                .TryCreateAuthenticatedClientProof(
                    bindingChallenge,
                    proofKey,
                    proof,
                    out var proofBytes) &&
            proofBytes == proof.Length,
            "authenticated UDP listener proof");
        await client.SendToAsync(
            proof,
            SocketFlags.None,
            endpoint,
            lifetime.Token);
        await WaitUntilBoundAsync(authority, lifetime.Token);
        Check.Equal(
            1,
            authority.GetSnapshot().BoundSessions,
            "UDP listener publishes one authenticated binding");

        lifetime.Cancel();
        await runTask;
        using var rebound = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        rebound.Bind(endpoint);
        lease.Dispose();
        Check.Equal(
            0,
            authority.GetSnapshot().TrackedSessions,
            "TLS lease revokes UDP listener session");
        Array.Clear(proofKey);
    }

    private static async Task<byte[]> ReceiveExactChallengeAsync(
        Socket client,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128];
        var received = await client.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            ReceiveTemplate,
            cancellationToken);
        Check.Equal(128, received.ReceivedBytes, "UDP challenge length");
        return buffer;
    }

    private static async Task WaitUntilBoundAsync(
        SecureUdpSessionAuthority authority,
        CancellationToken cancellationToken)
    {
        while (authority.GetSnapshot().BoundSessions == 0)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                cancellationToken);
        }
    }

    private static void CheckRateLimiterBounds()
    {
        var time = new ManualTimeProvider();
        var limiter = new SecureUdpRateLimiter(
            globalLimit: 3,
            unvalidatedLimit: 3,
            prefixLimit: 2,
            prefixCapacity: 2,
            bindingProofLimit: 3,
            bindingProofPrefixLimit: 2,
            protectedCandidateLimit: 3,
            protectedCandidatePrefixLimit: 2,
            authenticatedSessionLimit: 3,
            authenticatedSessionCapacity: 2,
            time);
        Check.True(
            limiter.TryAcquire(IPAddress.Parse("203.0.113.1")) &&
            limiter.TryAcquire(IPAddress.Parse("203.0.113.2")) &&
            !limiter.TryAcquire(IPAddress.Parse("203.0.113.3")),
            "UDP /24 prefix bound");
        Check.True(
            limiter.TryAcquire(IPAddress.Parse("198.51.100.1")) &&
            !limiter.TryAcquire(IPAddress.Parse("192.0.2.1")),
            "UDP global and prefix-table bounds");
        var snapshot = limiter.GetSnapshot();
        Check.True(
            snapshot.CurrentPackets == 3 &&
            snapshot.ActivePrefixes == 2,
            "UDP limiter state remains bounded");

        time.Advance(TimeSpan.FromSeconds(1));
        Check.True(
            limiter.TryAcquire(IPAddress.Parse("192.0.2.1")),
            "UDP limiter window recovers");
    }

    private static void CheckUnknownProtectedCandidateAdmission()
    {
        var connectionId = Enumerable.Range(1, 16)
            .Select(static value => checked((byte)value))
            .ToArray();
        using var authority = new SecureUdpSessionAuthority(
            capacity: 1,
            pendingTtl: TimeSpan.FromSeconds(30));
        using var cookies = new SecureUdpCookieProtector(
            new SecureUdpCookiePolicy(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(60)),
            serverId: 100,
            udpPort: 7_444,
            audience: "reborn-game");
        using var addressValidation =
            new SecureUdpAddressValidation(1_200, cookies);
        var limiter = new SecureUdpRateLimiter(
            globalLimit: 1,
            unvalidatedLimit: 1,
            prefixLimit: 1,
            prefixCapacity: 1,
            bindingProofLimit: 1,
            bindingProofPrefixLimit: 1,
            protectedCandidateLimit: 1,
            protectedCandidatePrefixLimit: 1,
            authenticatedSessionLimit: 1,
            authenticatedSessionCapacity: 1);
        var server = new SecureUdpEndpointServer(
            "127.0.0.1",
            port: 0,
            maximumDatagramBytes: 1_200,
            new SecureUdpBindingCoordinator(
                addressValidation,
                authority),
            limiter,
            authority);
        using var client = new SecureUdpProtectedSession(
            SecureUdpPeerRole.Client,
            Enumerable.Repeat((byte)0x52, 32).ToArray(),
            connectionId,
            serverId: 100,
            previousEpochOverlap: TimeSpan.FromSeconds(10));
        Span<byte> pingPayload = stackalloc byte[
            SecureUdpProtectedConstants.PingPayloadBytes];
        BinaryPrimitives.WriteUInt64BigEndian(pingPayload, 1);
        Span<byte> datagram = stackalloc byte[128];
        Check.True(
            client.TryProtect(
                SecureUdpProtectedMessageType.Ping,
                pingPayload,
                datagram,
                out var datagramBytes,
                out _),
            "unknown-session protected candidate encodes");
        var remote = new IPEndPoint(
            IPAddress.Parse("203.0.113.90"),
            50_090);
        Span<byte> response = stackalloc byte[128];
        var first = server.ProcessDatagram(
            datagram[..datagramBytes],
            remote,
            response);
        var afterFirst = limiter.GetSnapshot();
        authority.Dispose();
        var second = server.ProcessDatagram(
            datagram[..datagramBytes],
            remote,
            response);
        var afterSecond = limiter.GetSnapshot();
        Check.True(
            first.Outcome ==
                SecureUdpDatagramOutcome.ProtectedRejected &&
            second.Outcome == SecureUdpDatagramOutcome.RateLimited &&
            afterFirst.ProtectedCandidatePackets == 1 &&
            afterFirst.ActiveAuthenticatedSessions == 0 &&
            afterSecond.ProtectedCandidatePackets == 1 &&
            afterSecond.ActiveAuthenticatedSessions == 0,
            "unknown IDs exhaust bounded pre-auth admission before authority state is touched");
    }
}
