using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureUdpSessionAuthorityChecks
{
    private static void CheckRebindAndProtectedSessionContinuity()
    {
        var time = CreateTime();
        using var authority = new SecureUdpSessionAuthority(
            capacity: 1,
            pendingTtl: TimeSpan.FromSeconds(30),
            boundIdleTimeout: TimeSpan.FromSeconds(30),
            minimumRebindInterval: TimeSpan.FromSeconds(2),
            serverId: 100,
            previousEpochOverlap: TimeSpan.FromSeconds(10),
            time);
        var connection = CreateConnection(121);
        var principal = CreatePrincipal(121);
        var registration = authority.Register(connection, principal);
        var grant = CopyGrant(registration.Lease!);
        try
        {
            var firstEndpoint = new IPEndPoint(
                IPAddress.Parse("192.0.2.121"),
                51_121);
            var secondEndpoint = new IPEndPoint(
                IPAddress.Parse("198.51.100.121"),
                52_121);
            var firstChallenge = CreateRuntimeChallenge(
                grant.ConnectionId,
                nonceMarker: 0x11);
            var firstProof = ComputeProof(
                grant.ProofKey,
                firstChallenge);
            var status = authority.TryBind(
                grant.ConnectionId,
                firstChallenge,
                firstProof,
                firstEndpoint,
                out var bound,
                out var revision);
            Check.True(
                status == SecureUdpSessionBindStatus.Bound &&
                ReferenceEquals(principal, bound) &&
                revision == 1,
                "first endpoint starts binding revision one");

            var firstProtected = ProtectBindingConfirmation(
                authority,
                grant.ConnectionId,
                firstEndpoint,
                revision,
                nonceMarker: 0x11);
            Check.True(
                SecureUdpProtectedCodec.TryDecodeHeader(
                    firstProtected,
                    out var firstHeader) &&
                firstHeader.Sequence == 0 &&
                firstHeader.KeyEpoch ==
                    SecureUdpProtectedConstants.InitialKeyEpoch,
                "first protected confirmation starts one channel");

            var secondChallenge = CreateRuntimeChallenge(
                grant.ConnectionId,
                nonceMarker: 0x22);
            var secondProof = ComputeProof(
                grant.ProofKey,
                secondChallenge);
            status = authority.TryBind(
                grant.ConnectionId,
                secondChallenge,
                secondProof,
                secondEndpoint,
                out bound,
                out revision);
            Check.True(
                status ==
                    SecureUdpSessionBindStatus.RebindRateLimited &&
                bound is null &&
                revision == 1,
                "fresh endpoint proof cannot churn inside rebind interval");

            time.Advance(TimeSpan.FromSeconds(2));
            status = authority.TryBind(
                grant.ConnectionId,
                secondChallenge,
                secondProof,
                secondEndpoint,
                out bound,
                out revision);
            Check.True(
                status == SecureUdpSessionBindStatus.ReplayRejected &&
                bound is null,
                "rate-limited proof cannot be replayed after the interval");

            var freshChallenge = CreateRuntimeChallenge(
                grant.ConnectionId,
                nonceMarker: 0x33,
                issuedAtUnixSeconds: 3);
            var freshProof = ComputeProof(
                grant.ProofKey,
                freshChallenge);
            status = authority.TryBind(
                grant.ConnectionId,
                freshChallenge,
                freshProof,
                secondEndpoint,
                out bound,
                out revision);
            Check.True(
                status == SecureUdpSessionBindStatus.Rebound &&
                ReferenceEquals(principal, bound) &&
                revision == 2,
                "fresh cookie and TLS proof rebind after minimum interval");

            var secondProtected = ProtectBindingConfirmation(
                authority,
                grant.ConnectionId,
                secondEndpoint,
                revision,
                nonceMarker: 0x33);
            Check.True(
                SecureUdpProtectedCodec.TryDecodeHeader(
                    secondProtected,
                    out var secondHeader) &&
                secondHeader.Sequence == 1 &&
                secondHeader.KeyEpoch == firstHeader.KeyEpoch,
                "rebind preserves protected sequence and key epoch");

            time.Advance(TimeSpan.FromSeconds(2));
            status = authority.TryBind(
                grant.ConnectionId,
                firstChallenge,
                firstProof,
                firstEndpoint,
                out bound,
                out revision);
            Check.True(
                status == SecureUdpSessionBindStatus.ReplayRejected &&
                bound is null &&
                revision == 2,
                "captured prior proof cannot roll endpoint state back");
            Check.True(
                !authority.IsBoundEndpoint(
                    grant.ConnectionId,
                    firstEndpoint) &&
                authority.IsBoundEndpoint(
                    grant.ConnectionId,
                    secondEndpoint),
                "replay rejection retains the fresh endpoint");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static void CheckKeepaliveActivityAndIdleCleanup()
    {
        var time = CreateTime();
        using var authority = new SecureUdpSessionAuthority(
            capacity: 1,
            pendingTtl: TimeSpan.FromSeconds(30),
            boundIdleTimeout: TimeSpan.FromSeconds(15),
            minimumRebindInterval: TimeSpan.FromSeconds(2),
            serverId: 100,
            previousEpochOverlap: TimeSpan.FromSeconds(10),
            time);
        var connection = CreateConnection(122);
        var registration = authority.Register(
            connection,
            CreatePrincipal(122));
        var grant = CopyGrant(registration.Lease!);
        try
        {
            var endpoint = new IPEndPoint(
                IPAddress.Parse("203.0.113.122"),
                53_122);
            var challenge = CreateRuntimeChallenge(
                grant.ConnectionId,
                nonceMarker: 0x44);
            var proof = ComputeProof(grant.ProofKey, challenge);
            Check.True(
                authority.TryBind(
                    grant.ConnectionId,
                    challenge,
                    proof,
                    endpoint,
                    out _,
                    out var revision) ==
                    SecureUdpSessionBindStatus.Bound &&
                revision == 1,
                "idle-cleanup fixture binds");

            using var client = new SecureUdpProtectedSession(
                SecureUdpPeerRole.Client,
                grant.ProofKey,
                grant.ConnectionId,
                serverId: 100,
                previousEpochOverlap: TimeSpan.FromSeconds(10),
                time);
            time.Advance(TimeSpan.FromSeconds(14));
            Span<byte> ping = stackalloc byte[
                SecureUdpProtectedConstants.PingPayloadBytes];
            BinaryPrimitives.WriteUInt64BigEndian(ping, 7);
            BinaryPrimitives.WriteUInt64BigEndian(ping[8..], 14_000);
            Span<byte> datagram = stackalloc byte[128];
            Check.True(
                client.TryProtect(
                    SecureUdpProtectedMessageType.Ping,
                    ping,
                    datagram,
                    out var datagramBytes,
                    out _) &&
                authority.TryUnprotect(
                    SecureUdpConnectionKeyFrom(grant.ConnectionId),
                    endpoint,
                    datagram[..datagramBytes],
                    stackalloc byte[
                        SecureUdpProtectedConstants
                            .MaximumPayloadBytes]).IsAccepted,
                "authenticated keepalive refreshes bound activity");

            time.Advance(TimeSpan.FromSeconds(14));
            Check.Equal(
                0,
                authority.CleanupExpiredSessions(),
                "active protected session survives before idle boundary");
            time.Advance(TimeSpan.FromSeconds(1));
            Check.Equal(
                1,
                authority.CleanupExpiredSessions(),
                "bound session expires exactly at idle boundary");
            Check.Equal(
                0,
                authority.GetSnapshot().TrackedSessions,
                "idle cleanup releases bounded session state");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static void CheckOutboundTrafficDoesNotRefreshInboundLiveness()
    {
        var time = CreateTime();
        using var authority = new SecureUdpSessionAuthority(
            capacity: 1,
            pendingTtl: TimeSpan.FromSeconds(30),
            boundIdleTimeout: TimeSpan.FromSeconds(15),
            minimumRebindInterval: TimeSpan.FromSeconds(2),
            serverId: 100,
            previousEpochOverlap: TimeSpan.FromSeconds(10),
            time);
        var connection = CreateConnection(123);
        var registration = authority.Register(
            connection,
            CreatePrincipal(123));
        var grant = CopyGrant(registration.Lease!);
        try
        {
            var endpoint = new IPEndPoint(
                IPAddress.Parse("203.0.113.123"),
                53_123);
            var challenge = CreateRuntimeChallenge(
                grant.ConnectionId,
                nonceMarker: 0x55);
            var proof = ComputeProof(grant.ProofKey, challenge);
            Check.True(
                authority.TryBind(
                    grant.ConnectionId,
                    challenge,
                    proof,
                    endpoint,
                    out _,
                    out var revision) ==
                    SecureUdpSessionBindStatus.Bound &&
                revision == 1,
                "outbound-idle fixture binds");

            time.Advance(TimeSpan.FromSeconds(14));
            Span<byte> pong = stackalloc byte[
                SecureUdpProtectedConstants.PongPayloadBytes];
            BinaryPrimitives.WriteUInt64BigEndian(pong, 1);
            BinaryPrimitives.WriteUInt64BigEndian(pong[16..], 14_000);
            BinaryPrimitives.WriteUInt64BigEndian(pong[24..], 14_001);
            Check.True(
                authority.TryProtect(
                    SecureUdpConnectionKeyFrom(grant.ConnectionId),
                    endpoint,
                    revision,
                    SecureUdpProtectedMessageType.Pong,
                    pong,
                    stackalloc byte[128],
                    out _,
                    out _,
                    out _),
                "server may send before the inbound idle boundary");

            time.Advance(TimeSpan.FromSeconds(1));
            Check.Equal(
                1,
                authority.CleanupExpiredSessions(),
                "outbound protection does not extend authenticated receive liveness");
            Check.Equal(
                0,
                authority.GetSnapshot().TrackedSessions,
                "server-only traffic cannot retain an idle UDP session");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static byte[] CreateRuntimeChallenge(
        ReadOnlySpan<byte> connectionId,
        byte nonceMarker,
        long issuedAtUnixSeconds = 1)
    {
        var nonce = Enumerable.Repeat(
            nonceMarker,
            SecureUdpBindingConstants.ClientNonceBytes).ToArray();
        var cookie = Enumerable.Range(
                1,
                SecureUdpBindingConstants.CookieTagBytes)
            .Select(value => checked((byte)(value ^ nonceMarker)))
            .ToArray();
        var output = new byte[
            SecureUdpBindingConstants.DatagramBytes];
        Check.True(
            SecureUdpBindingCodec.TryEncode(
                SecureUdpBindingType.ServerChallenge,
                connectionId,
                keyEpoch: 7,
                sequence: 0,
                nonce,
                issuedAtUnixSeconds,
                cookie,
                output,
                out var written) &&
            written == output.Length,
            "rebind challenge fixture encodes");
        return output;
    }

    private static byte[] ProtectBindingConfirmation(
        SecureUdpSessionAuthority authority,
        ReadOnlySpan<byte> connectionId,
        IPEndPoint endpoint,
        ulong revision,
        byte nonceMarker)
    {
        Span<byte> payload = stackalloc byte[
            SecureUdpProtectedConstants.BindingConfirmPayloadBytes];
        payload[..16].Fill(nonceMarker);
        BinaryPrimitives.WriteUInt64BigEndian(payload[16..], revision);
        BinaryPrimitives.WriteUInt64BigEndian(payload[24..], 1);
        var output = new byte[128];
        Check.True(
            authority.TryProtect(
                SecureUdpConnectionKeyFrom(connectionId),
                endpoint,
                revision,
                SecureUdpProtectedMessageType.BindingConfirm,
                payload,
                output,
                out var written,
                out _,
                out _) &&
            written > 0,
            "authority protects binding confirmation");
        return output[..written];
    }

    private static SecureUdpConnectionKey SecureUdpConnectionKeyFrom(
        ReadOnlySpan<byte> connectionId)
    {
        Check.True(
            SecureUdpConnectionKey.TryCreate(
                connectionId,
                out var key),
            "test connection ID is canonical");
        return key;
    }
}
