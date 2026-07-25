using System.Net;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpBindingCoordinatorChecks
{
    public static void Run()
    {
        CheckHelloAndAuthenticatedProof();
        CheckCookieOnlyAndTamperedProofs();
        CheckUnknownRevokedAndExpiredProofs();
        CheckEndpointConflict();
    }

    private static void CheckHelloAndAuthenticatedProof()
    {
        using var fixture = CoordinatorFixture.Create(capacity: 1);
        var connection =
            SecureUdpSessionAuthorityChecks.CreateTestConnection(71);
        var principal =
            SecureUdpSessionAuthorityChecks.CreateTestPrincipal(71);
        var registration = fixture.Authority.Register(
            connection,
            principal);
        var grant = SecureUdpSessionAuthorityChecks.CopyTestGrant(
            registration.Lease!);
        try
        {
            var endpoint = new IPEndPoint(
                IPAddress.Parse("192.0.2.71"),
                55_071);
            var challenge = IssueChallenge(
                fixture.Coordinator,
                connection.ConnectionId.Span,
                endpoint);
            var proof = CreateAuthenticatedProof(
                challenge,
                grant.ProofKey);
            var response = Enumerable.Repeat(
                    (byte)0xA5,
                    SecureUdpBindingConstants.DatagramBytes)
                .ToArray();
            var result = fixture.Coordinator.ProcessDatagram(
                proof,
                endpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.Bound &&
                result.ResponseBytes == 0 &&
                !result.HasResponse &&
                ReferenceEquals(principal, result.Principal),
                "authenticated type-4 proof binds with no response");
            Check.Equal(
                1,
                fixture.Authority.GetSnapshot().BoundSessions,
                "coordinator publishes one bound authority session");

            result = fixture.Coordinator.ProcessDatagram(
                proof,
                endpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.AlreadyBound &&
                result.ResponseBytes == 0 &&
                ReferenceEquals(principal, result.Principal),
                "same coordinator proof is idempotent");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static void CheckCookieOnlyAndTamperedProofs()
    {
        using var fixture = CoordinatorFixture.Create(capacity: 1);
        var connection =
            SecureUdpSessionAuthorityChecks.CreateTestConnection(72);
        var registration = fixture.Authority.Register(
            connection,
            SecureUdpSessionAuthorityChecks.CreateTestPrincipal(72));
        var grant = SecureUdpSessionAuthorityChecks.CopyTestGrant(
            registration.Lease!);
        try
        {
            var endpoint = new IPEndPoint(
                IPAddress.Parse("198.51.100.72"),
                55_072);
            var challenge = IssueChallenge(
                fixture.Coordinator,
                connection.ConnectionId.Span,
                endpoint);
            var response = new byte[
                SecureUdpBindingConstants.DatagramBytes];
            var cookieOnly = new byte[
                SecureUdpBindingConstants.DatagramBytes];
            Check.True(
                SecureUdpAddressValidation.TryCreateClientProof(
                    challenge,
                    cookieOnly,
                    out var cookieOnlyBytes) &&
                cookieOnlyBytes == cookieOnly.Length,
                "cookie-only type-3 proof fixture encodes");

            var result = fixture.Coordinator.ProcessDatagram(
                cookieOnly,
                endpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.Rejected &&
                result.ResponseBytes == 0 &&
                result.Principal is null,
                "cookie-only type-3 proof never binds a TLS session");
            Check.Equal(
                1,
                fixture.Authority.GetSnapshot().PendingSessions,
                "type-3 rejection preserves pending authority state");

            var authenticated = CreateAuthenticatedProof(
                challenge,
                grant.ProofKey);
            var cookieTampered = authenticated.ToArray();
            cookieTampered[^1] ^= 0x01;
            result = fixture.Coordinator.ProcessDatagram(
                cookieTampered,
                endpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.Rejected &&
                result.ResponseBytes == 0,
                "cookie tamper rejects before authority binding");

            var proofTampered = authenticated.ToArray();
            proofTampered[72] ^= 0x01;
            result = fixture.Coordinator.ProcessDatagram(
                proofTampered,
                endpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.InvalidProof &&
                result.ResponseBytes == 0,
                "TLS proof-tag tamper reaches finite invalid-proof result");
            Check.Equal(
                1,
                fixture.Authority.GetSnapshot().PendingSessions,
                "tampered proofs consume no authority state");

            result = fixture.Coordinator.ProcessDatagram(
                authenticated.AsSpan(0, authenticated.Length - 1),
                endpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.Rejected &&
                result.ResponseBytes == 0,
                "truncated authenticated datagram is silent");

            result = fixture.Coordinator.ProcessDatagram(
                challenge,
                endpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.Rejected &&
                result.ResponseBytes == 0,
                "server challenge cannot be reflected through coordinator");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static void CheckUnknownRevokedAndExpiredProofs()
    {
        CheckUnknownProof();
        CheckRevokedProof();
        CheckExpiredProof();
    }

    private static void CheckUnknownProof()
    {
        using var fixture = CoordinatorFixture.Create(capacity: 1);
        var connection =
            SecureUdpSessionAuthorityChecks.CreateTestConnection(81);
        var endpoint = new IPEndPoint(
            IPAddress.Parse("203.0.113.81"),
            56_081);
        var challenge = IssueChallenge(
            fixture.Coordinator,
            connection.ConnectionId.Span,
            endpoint);
        var unknownKey =
            SecureUdpSessionAuthorityChecks.CreateTestProofKey(81);
        try
        {
            var proof = CreateAuthenticatedProof(
                challenge,
                unknownKey);
            var result = fixture.Coordinator.ProcessDatagram(
                proof,
                endpoint,
                new byte[SecureUdpBindingConstants.DatagramBytes]);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.UnknownSession &&
                result.ResponseBytes == 0 &&
                result.Principal is null,
                "valid cookie and proof for unknown TLS ID reject silently");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unknownKey);
        }
    }

    private static void CheckRevokedProof()
    {
        using var fixture = CoordinatorFixture.Create(capacity: 1);
        var connection =
            SecureUdpSessionAuthorityChecks.CreateTestConnection(82);
        var registration = fixture.Authority.Register(
            connection,
            SecureUdpSessionAuthorityChecks.CreateTestPrincipal(82));
        var grant = SecureUdpSessionAuthorityChecks.CopyTestGrant(
            registration.Lease!);
        try
        {
            var endpoint = new IPEndPoint(
                IPAddress.Parse("203.0.113.82"),
                56_082);
            var challenge = IssueChallenge(
                fixture.Coordinator,
                connection.ConnectionId.Span,
                endpoint);
            var proof = CreateAuthenticatedProof(
                challenge,
                grant.ProofKey);
            registration.Lease!.Dispose();
            var result = fixture.Coordinator.ProcessDatagram(
                proof,
                endpoint,
                new byte[SecureUdpBindingConstants.DatagramBytes]);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.UnknownSession &&
                result.ResponseBytes == 0 &&
                result.Principal is null,
                "revoked TLS registration cannot bind afterward");
        }
        finally
        {
            grant.Clear();
        }
    }

    private static void CheckExpiredProof()
    {
        using var fixture = CoordinatorFixture.Create(capacity: 1);
        var connection =
            SecureUdpSessionAuthorityChecks.CreateTestConnection(83);
        var registration = fixture.Authority.Register(
            connection,
            SecureUdpSessionAuthorityChecks.CreateTestPrincipal(83));
        var grant = SecureUdpSessionAuthorityChecks.CopyTestGrant(
            registration.Lease!);
        try
        {
            var endpoint = new IPEndPoint(
                IPAddress.Parse("203.0.113.83"),
                56_083);
            var challenge = IssueChallenge(
                fixture.Coordinator,
                connection.ConnectionId.Span,
                endpoint);
            var proof = CreateAuthenticatedProof(
                challenge,
                grant.ProofKey);
            fixture.Time.Advance(TimeSpan.FromSeconds(5));
            var result = fixture.Coordinator.ProcessDatagram(
                proof,
                endpoint,
                new byte[SecureUdpBindingConstants.DatagramBytes]);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.Expired &&
                result.ResponseBytes == 0 &&
                result.Principal is null,
                "expired TLS binding offer cannot bind");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static void CheckEndpointConflict()
    {
        using var fixture = CoordinatorFixture.Create(capacity: 1);
        var connection =
            SecureUdpSessionAuthorityChecks.CreateTestConnection(91);
        var principal =
            SecureUdpSessionAuthorityChecks.CreateTestPrincipal(91);
        var registration = fixture.Authority.Register(
            connection,
            principal);
        var grant = SecureUdpSessionAuthorityChecks.CopyTestGrant(
            registration.Lease!);
        try
        {
            var firstEndpoint = new IPEndPoint(
                IPAddress.Parse("192.0.2.91"),
                57_091);
            var firstChallenge = IssueChallenge(
                fixture.Coordinator,
                connection.ConnectionId.Span,
                firstEndpoint);
            var firstProof = CreateAuthenticatedProof(
                firstChallenge,
                grant.ProofKey);
            var response = new byte[
                SecureUdpBindingConstants.DatagramBytes];
            Check.True(
                fixture.Coordinator.ProcessDatagram(
                    firstProof,
                    firstEndpoint,
                    response).Outcome ==
                    SecureUdpBindingProcessOutcome.Bound,
                "endpoint-conflict fixture binds original endpoint");

            var secondEndpoint = new IPEndPoint(
                IPAddress.Parse("192.0.2.92"),
                57_092);
            var secondChallenge = IssueChallenge(
                fixture.Coordinator,
                connection.ConnectionId.Span,
                secondEndpoint);
            var secondProof = CreateAuthenticatedProof(
                secondChallenge,
                grant.ProofKey);
            var result = fixture.Coordinator.ProcessDatagram(
                secondProof,
                secondEndpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome
                        .RebindRateLimited &&
                result.ResponseBytes == 0 &&
                result.Principal is null,
                "new endpoint cannot churn an active binding immediately");

            result = fixture.Coordinator.ProcessDatagram(
                firstProof,
                firstEndpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.AlreadyBound &&
                ReferenceEquals(principal, result.Principal),
                "endpoint takeover attempt preserves original principal");

            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            var freshChallenge = IssueChallenge(
                fixture.Coordinator,
                connection.ConnectionId.Span,
                secondEndpoint);
            var freshProof = CreateAuthenticatedProof(
                freshChallenge,
                grant.ProofKey);
            result = fixture.Coordinator.ProcessDatagram(
                freshProof,
                secondEndpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.Rebound &&
                ReferenceEquals(principal, result.Principal) &&
                result.BindingRevision == 2 &&
                result.ResponseBytes == 0,
                "fresh return-path and TLS proof perform guarded rebind");

            result = fixture.Coordinator.ProcessDatagram(
                firstProof,
                firstEndpoint,
                response);
            Check.True(
                result.Outcome ==
                    SecureUdpBindingProcessOutcome.ReplayRejected &&
                result.Principal is null &&
                result.BindingRevision == 2,
                "captured original proof cannot roll rebind back");
        }
        finally
        {
            grant.Clear();
            registration.Lease!.Dispose();
        }
    }

    private static byte[] IssueChallenge(
        SecureUdpBindingCoordinator coordinator,
        ReadOnlySpan<byte> connectionId,
        IPEndPoint endpoint)
    {
        var nonce = Enumerable.Range(
                1,
                SecureUdpBindingConstants.ClientNonceBytes)
            .Select(static value => checked((byte)(value + 120)))
            .ToArray();
        var hello = new byte[
            SecureUdpBindingConstants.DatagramBytes];
        Check.True(
            SecureUdpAddressValidation.TryEncodeClientHello(
                connectionId,
                nonce,
                hello,
                out var helloBytes) &&
            helloBytes == hello.Length,
            "coordinator hello fixture encodes");

        var challenge = new byte[
            SecureUdpBindingConstants.DatagramBytes];
        var result = coordinator.ProcessDatagram(
            hello,
            endpoint,
            challenge);
        Check.True(
            result.Outcome ==
                SecureUdpBindingProcessOutcome.ChallengeCreated &&
            result.ResponseBytes == hello.Length &&
            result.HasResponse &&
            result.Principal is null,
            "valid hello receives exact non-amplifying challenge");
        return challenge;
    }

    private static byte[] CreateAuthenticatedProof(
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> proofKey)
    {
        var proof = new byte[
            SecureUdpBindingConstants.DatagramBytes];
        Check.True(
            SecureUdpAddressValidation
                .TryCreateAuthenticatedClientProof(
                    challenge,
                    proofKey,
                    proof,
                    out var written) &&
            written == proof.Length,
            "authenticated type-4 proof fixture encodes");
        return proof;
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly SecureUdpAddressValidation _validation;

        private CoordinatorFixture(
            ManualTimeProvider time,
            SecureUdpSessionAuthority authority,
            SecureUdpAddressValidation validation)
        {
            Time = time;
            Authority = authority;
            _validation = validation;
            Coordinator = new SecureUdpBindingCoordinator(
                validation,
                authority);
        }

        public ManualTimeProvider Time { get; }

        public SecureUdpSessionAuthority Authority { get; }

        public SecureUdpBindingCoordinator Coordinator { get; }

        public static CoordinatorFixture Create(int capacity)
        {
            var time =
                SecureUdpSessionAuthorityChecks.CreateTestTime();
            var authority =
                SecureUdpSessionAuthorityChecks.CreateTestAuthority(
                    capacity,
                    time);
            var policy = new SecureUdpCookiePolicy(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(60));
            var cookieSecret = Enumerable.Repeat(
                    (byte)0x5A,
                    SecureUdpCookieKeyRing.SecretBytes)
                .ToArray();
            var keyRing = new SecureUdpCookieKeyRing(
                time,
                policy.KeyRotation,
                () => cookieSecret.ToArray(),
                () => 0x10203040);
            var protector = new SecureUdpCookieProtector(
                policy,
                serverId: 100,
                udpPort: 7444,
                audience: "reborn-game",
                time,
                keyRing);
            var validation = new SecureUdpAddressValidation(
                SecureUdpBindingConstants.MaximumDatagramBytes,
                protector);
            CryptographicOperations.ZeroMemory(cookieSecret);
            return new CoordinatorFixture(
                time,
                authority,
                validation);
        }

        public void Dispose()
        {
            _validation.Dispose();
            Authority.Dispose();
        }
    }
}
