using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureUdpSessionAuthorityChecks
{
    private static void CheckProofAuthenticationAndEndpointBinding()
    {
        var time = CreateTime();
        using var authority = CreateAuthority(2, time);
        var connection = CreateConnection(21);
        var principal = CreatePrincipal(21);
        var registration = authority.Register(connection, principal);
        Check.True(
            registration.IsRegistered,
            "proof-authentication UDP session registers");

        var grant = CopyGrant(registration.Lease!);
        try
        {
            var challenge = CreateChallenge(connection.ConnectionId.Span);
            var authenticator = ComputeProof(
                grant.ProofKey,
                challenge);
            var originalEndpoint = new IPEndPoint(
                IPAddress.Parse("192.0.2.21"),
                50_021);

            var status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                authenticator,
                originalEndpoint,
                out var boundPrincipal);
            Check.True(
                status == SecureUdpSessionBindStatus.Bound &&
                ReferenceEquals(principal, boundPrincipal),
                "valid TLS proof binds its authenticated principal");
            Check.Equal(
                new SecureUdpSessionAuthoritySnapshot(2, 0, 1),
                authority.GetSnapshot(),
                "successful proof moves one pending session to bound");

            status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                authenticator,
                originalEndpoint,
                out boundPrincipal);
            Check.True(
                status == SecureUdpSessionBindStatus.AlreadyBound &&
                ReferenceEquals(principal, boundPrincipal),
                "same-endpoint proof replay is idempotent");

            var conflictingEndpoint = new IPEndPoint(
                IPAddress.Parse("192.0.2.22"),
                50_022);
            status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                authenticator,
                conflictingEndpoint,
                out boundPrincipal);
            Check.True(
                status == SecureUdpSessionBindStatus.EndpointConflict &&
                boundPrincipal is null,
                "different endpoint cannot replace an active binding");

            status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                authenticator,
                originalEndpoint,
                out boundPrincipal);
            Check.True(
                status == SecureUdpSessionBindStatus.AlreadyBound &&
                ReferenceEquals(principal, boundPrincipal),
                "endpoint conflict preserves the original binding");

            status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                authenticator,
                new IPEndPoint(IPAddress.Any, 50_021),
                out boundPrincipal);
            Check.True(
                status == SecureUdpSessionBindStatus.InvalidEndpoint &&
                boundPrincipal is null,
                "unspecified endpoint cannot bind a session");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static void CheckUnknownRevokedAndExpiredProofs()
    {
        CheckWrongKeyAndTag();
        CheckUnknownAndRevokedProofs();
        CheckExpiredProof();
    }

    private static void CheckWrongKeyAndTag()
    {
        var time = CreateTime();
        using var authority = CreateAuthority(1, time);
        var connection = CreateConnection(31);
        var registration = authority.Register(
            connection,
            CreatePrincipal(31));
        var grant = CopyGrant(registration.Lease!);
        try
        {
            var challenge = CreateChallenge(connection.ConnectionId.Span);
            var wrongKey = CreateProofKey(99);
            var wrongAuthenticator = ComputeProof(wrongKey, challenge);
            CryptographicOperations.ZeroMemory(wrongKey);
            var endpoint = new IPEndPoint(
                IPAddress.Parse("198.51.100.31"),
                51_031);

            var status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                wrongAuthenticator,
                endpoint,
                out var principal);
            Check.True(
                status == SecureUdpSessionBindStatus.InvalidProof &&
                principal is null,
                "proof produced with a different TLS key rejects");

            var validAuthenticator = ComputeProof(
                grant.ProofKey,
                challenge);
            validAuthenticator[^1] ^= 0x01;
            status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                validAuthenticator,
                endpoint,
                out principal);
            Check.True(
                status == SecureUdpSessionBindStatus.InvalidProof &&
                principal is null,
                "single-bit TLS proof-tag mutation rejects");
            Check.Equal(
                1,
                authority.GetSnapshot().PendingSessions,
                "invalid proof does not consume pending session");

            status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                validAuthenticator.AsSpan(0, 23),
                endpoint,
                out principal);
            Check.True(
                status == SecureUdpSessionBindStatus.InvalidProof &&
                principal is null,
                "truncated TLS proof tag rejects");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static void CheckUnknownAndRevokedProofs()
    {
        var time = CreateTime();
        using var authority = CreateAuthority(1, time);
        var endpoint = new IPEndPoint(
            IPAddress.Parse("203.0.113.41"),
            52_041);

        var unknownConnection = CreateConnection(41);
        var unknownChallenge = CreateChallenge(
            unknownConnection.ConnectionId.Span);
        var unknownKey = CreateProofKey(41);
        var unknownProof = ComputeProof(
            unknownKey,
            unknownChallenge);
        CryptographicOperations.ZeroMemory(unknownKey);
        var status = authority.TryBind(
            unknownConnection.ConnectionId.Span,
            unknownChallenge,
            unknownProof,
            endpoint,
            out var principal);
        Check.True(
            status == SecureUdpSessionBindStatus.UnknownSession &&
            principal is null,
            "unknown TLS connection ID rejects");

        var connection = CreateConnection(42);
        var registration = authority.Register(
            connection,
            CreatePrincipal(42));
        var grant = CopyGrant(registration.Lease!);
        try
        {
            var challenge = CreateChallenge(connection.ConnectionId.Span);
            var authenticator = ComputeProof(
                grant.ProofKey,
                challenge);
            registration.Lease!.Dispose();
            status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                authenticator,
                endpoint,
                out principal);
            Check.True(
                status == SecureUdpSessionBindStatus.UnknownSession &&
                principal is null,
                "revoked TLS session cannot bind with a captured proof");
        }
        finally
        {
            grant.Clear();
        }
    }

    private static void CheckExpiredProof()
    {
        var time = CreateTime();
        byte[]? capturedKey = null;
        using var authority = new SecureUdpSessionAuthority(
            1,
            TimeSpan.FromSeconds(5),
            time,
            () => capturedKey = CreateProofKey(51));
        var connection = CreateConnection(51);
        var registration = authority.Register(
            connection,
            CreatePrincipal(51));
        var grant = CopyGrant(registration.Lease!);
        try
        {
            var challenge = CreateChallenge(connection.ConnectionId.Span);
            var authenticator = ComputeProof(
                grant.ProofKey,
                challenge);
            time.Advance(TimeSpan.FromSeconds(5));
            var status = authority.TryBind(
                connection.ConnectionId.Span,
                challenge,
                authenticator,
                new IPEndPoint(
                    IPAddress.Parse("203.0.113.51"),
                    53_051),
                out var principal);
            Check.True(
                status == SecureUdpSessionBindStatus.Expired &&
                principal is null,
                "proof at the pending-offer deadline rejects as expired");
            Check.Equal(
                0,
                authority.GetSnapshot().TrackedSessions,
                "expired proof removes pending authority state");
            Check.True(
                capturedKey is not null &&
                SecureUdpBindingCodec.IsAllZero(capturedKey),
                "expired proof zeroes authority-owned key material");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static void CheckSecretZeroing()
    {
        var time = CreateTime();
        var capturedKeys = new ConcurrentQueue<byte[]>();
        var next = 60;
        var authority = new SecureUdpSessionAuthority(
            2,
            TimeSpan.FromSeconds(5),
            time,
            () =>
            {
                var key = CreateProofKey(
                    Interlocked.Increment(ref next));
                capturedKeys.Enqueue(key);
                return key;
            });
        var pending = authority.Register(
            CreateConnection(61),
            CreatePrincipal(61));
        var boundConnection = CreateConnection(62);
        var bound = authority.Register(
            boundConnection,
            CreatePrincipal(62));
        var boundGrant = CopyGrant(bound.Lease!);
        try
        {
            var challenge = CreateChallenge(
                boundConnection.ConnectionId.Span);
            var authenticator = ComputeProof(
                boundGrant.ProofKey,
                challenge);
            Check.True(
                authority.TryBind(
                    boundConnection.ConnectionId.Span,
                    challenge,
                    authenticator,
                    new IPEndPoint(
                        IPAddress.Parse("192.0.2.62"),
                        54_062),
                    out _) == SecureUdpSessionBindStatus.Bound,
                "zeroing fixture binds one session");

            authority.Dispose();
            authority.Dispose();
            Check.True(
                capturedKeys.Count == 2 &&
                capturedKeys.All(static key =>
                    SecureUdpBindingCodec.IsAllZero(key)),
                "authority disposal zeroes pending and bound proof keys");
            Check.True(
                !pending.Lease!.TryCopyGrantMaterial(
                    stackalloc byte[
                        SecureUdpBindingConstants.ConnectionIdBytes],
                    stackalloc byte[
                        SecureUdpTlsProofAuthenticator.KeyBytes],
                    out _),
                "disposed authority exposes no grant material");
        }
        finally
        {
            boundGrant.Clear();
            pending.Lease!.Dispose();
            bound.Lease!.Dispose();
            authority.Dispose();
        }
    }

    private static CapturedGrant CopyGrant(
        SecureUdpSessionLease lease)
    {
        var connectionId = new byte[
            SecureUdpBindingConstants.ConnectionIdBytes];
        var proofKey = new byte[
            SecureUdpTlsProofAuthenticator.KeyBytes];
        Check.True(
            lease.TryCopyGrantMaterial(
                connectionId,
                proofKey,
                out var expiry),
            "UDP grant material copies");
        return new CapturedGrant(
            connectionId,
            proofKey,
            expiry);
    }

    private static byte[] CreateChallenge(
        ReadOnlySpan<byte> connectionId)
    {
        var nonce = Enumerable.Range(
                1,
                SecureUdpBindingConstants.ClientNonceBytes)
            .Select(static value => checked((byte)(value + 64)))
            .ToArray();
        var cookie = Enumerable.Range(
                1,
                SecureUdpBindingConstants.CookieTagBytes)
            .Select(static value => checked((byte)(value + 96)))
            .ToArray();
        var challenge = new byte[
            SecureUdpBindingConstants.DatagramBytes];
        Check.True(
            SecureUdpBindingCodec.TryEncode(
                SecureUdpBindingType.ServerChallenge,
                connectionId,
                keyEpoch: 7,
                sequence: 0,
                nonce,
                issuedAtUnixSeconds: 1,
                cookie,
                challenge,
                out var written) &&
            written == challenge.Length,
            "server challenge fixture encodes");
        return challenge;
    }

    private static byte[] ComputeProof(
        ReadOnlySpan<byte> proofKey,
        ReadOnlySpan<byte> challenge)
    {
        var output = new byte[
            SecureUdpBindingConstants.TlsProofTagBytes];
        Check.True(
            SecureUdpTlsProofAuthenticator.TryCompute(
                proofKey,
                challenge,
                output),
            "TLS proof fixture computes");
        return output;
    }

    internal static CapturedGrant CopyTestGrant(
        SecureUdpSessionLease lease) =>
        CopyGrant(lease);

    internal sealed record CapturedGrant(
        byte[] ConnectionId,
        byte[] ProofKey,
        ulong ExpiryUnixMilliseconds)
    {
        public void Clear()
        {
            CryptographicOperations.ZeroMemory(ConnectionId);
            CryptographicOperations.ZeroMemory(ProofKey);
        }
    }
}
