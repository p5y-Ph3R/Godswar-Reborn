using System.Net;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureUdpCookieChecks
{
    private static readonly byte[] ConnectionId =
        SecureUdpBindingCodecChecks.TestConnectionId.ToArray();

    private static readonly byte[] Nonce =
        SecureUdpBindingCodecChecks.TestNonce.ToArray();

    private static readonly IPEndPoint Remote =
        new(IPAddress.Parse("203.0.113.9"), 50_000);

    public static void Run()
    {
        CheckHappyPathAndEndpointBinding();
        CheckScopeAndTimeBinding();
        CheckRotationAndSecretLifecycle();
        CheckCookieAllocationBound();
        CheckConcurrentValidation();
        CheckAdversarialInputsAndDisposal();
    }

    private static void CheckHappyPathAndEndpointBinding()
    {
        using var fixture = CookieFixture.Create();
        var hello = EncodeHello(ConnectionId, Nonce);
        var challenge = new byte[128];
        Check.True(
            fixture.Service.TryCreateChallenge(
                hello,
                Remote,
                challenge,
                out var challengeBytes),
            "valid UDP hello receives challenge");
        Check.Equal(128, challengeBytes, "UDP challenge bytes");
        Check.True(
            challengeBytes <= hello.Length,
            "prevalidation amplification is at most one");
        Check.True(
            challenge.AsSpan(96, 32).SequenceEqual(
                Convert.FromHexString(
                    "9CDD4263579EF90AEE47F20C05EB7AE9" +
                    "FE28B2068E5EDC42CB63D712ADFFABD0")),
            "UDP cookie HMAC-SHA256 golden vector");
        Check.True(
            SecureUdpBindingCodec.TryDecode(
                challenge,
                out var decodedChallenge) &&
            decodedChallenge.Type ==
                SecureUdpBindingType.ServerChallenge,
            "server challenge is well formed");

        var proof = new byte[128];
        Check.True(
            SecureUdpAddressValidation.TryCreateClientProof(
                challenge,
                proof,
                out var proofBytes),
            "client proof echoes challenge");
        Check.Equal(128, proofBytes, "UDP proof bytes");
        var inPlaceProof = (byte[])challenge.Clone();
        Check.True(
            SecureUdpAddressValidation.TryCreateClientProof(
                inPlaceProof,
                inPlaceProof,
                out var inPlaceBytes) &&
            inPlaceBytes == inPlaceProof.Length,
            "client proof supports in-place challenge conversion");

        var validatedConnection = new byte[16];
        Check.True(
            fixture.Service.TryValidateClientProof(
                proof,
                Remote,
                validatedConnection),
            "valid UDP proof verifies");
        Check.True(
            validatedConnection.SequenceEqual(ConnectionId),
            "validated UDP connection ID");

        var mappedRemote = new IPEndPoint(
            IPAddress.Parse("::ffff:203.0.113.9"),
            Remote.Port);
        Check.True(
            fixture.Service.TryValidateClientProof(
                proof,
                mappedRemote,
                validatedConnection),
            "IPv4-mapped IPv6 canonicalizes to IPv4");
        Check.True(
            !fixture.Service.TryValidateClientProof(
                proof,
                new IPEndPoint(Remote.Address, Remote.Port + 1),
                validatedConnection),
            "UDP source-port change rejects");
        Check.True(
            !fixture.Service.TryValidateClientProof(
                proof,
                new IPEndPoint(
                    IPAddress.Parse("203.0.113.10"),
                    Remote.Port),
                validatedConnection),
            "UDP source-address change rejects");

        foreach (var offset in new[] { 12, 28, 48, 64, 96 })
        {
            var tampered = (byte[])proof.Clone();
            tampered[offset] ^= 0x01;
            Array.Fill(validatedConnection, (byte)0xCC);
            Check.True(
                !fixture.Service.TryValidateClientProof(
                    tampered,
                    Remote,
                    validatedConnection),
                $"UDP proof mutation at {offset} rejects");
            Check.True(
                validatedConnection.All(
                    static value => value == 0xCC),
                "failed proof does not publish connection ID");
        }
        for (var byteIndex = 96; byteIndex < 128; byteIndex++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var tampered = (byte[])proof.Clone();
                tampered[byteIndex] ^= checked((byte)(1 << bit));
                Check.True(
                    !fixture.Service.TryValidateClientProof(
                        tampered,
                        Remote,
                        validatedConnection),
                    $"UDP cookie tag bit {byteIndex - 96}:{bit} rejects");
            }
        }

        Check.True(
            !fixture.Service.TryCreateChallenge(
                hello,
                new IPEndPoint(IPAddress.Any, Remote.Port),
                challenge,
                out challengeBytes),
            "unspecified observed endpoint silently rejects");
        Check.Equal(0, challengeBytes, "invalid endpoint response bytes");
        foreach (var invalidAddress in new[]
        {
            IPAddress.Broadcast,
            IPAddress.Parse("224.0.0.1"),
            IPAddress.Parse("ff02::1")
        })
        {
            Check.True(
                !fixture.Service.TryCreateChallenge(
                    hello,
                    new IPEndPoint(invalidAddress, Remote.Port),
                    challenge,
                    out challengeBytes),
                $"invalid observed endpoint {invalidAddress} rejects");
            Check.Equal(
                0,
                challengeBytes,
                "invalid endpoint emits no challenge");
        }
    }

    private static void CheckScopeAndTimeBinding()
    {
        using var fixture = CookieFixture.Create();
        var proof = CreateProof(fixture, Remote);

        using var wrongAudience = CookieFixture.Create(
            timeProvider: fixture.Time,
            audience: "other-audience");
        Check.True(
            !wrongAudience.Service.TryValidateClientProof(
                proof,
                Remote,
                new byte[16]),
            "UDP cookie audience mismatch rejects");
        using var wrongServerPort = CookieFixture.Create(
            timeProvider: fixture.Time,
            udpPort: 7445);
        Check.True(
            !wrongServerPort.Service.TryValidateClientProof(
                proof,
                Remote,
                new byte[16]),
            "UDP destination-port scope mismatch rejects");
        using var wrongServer = CookieFixture.Create(
            timeProvider: fixture.Time,
            serverId: 101);
        Check.True(
            !wrongServer.Service.TryValidateClientProof(
                proof,
                Remote,
                new byte[16]),
            "UDP target-server scope mismatch rejects");

        var scopedAddress = new IPAddress(
            IPAddress.Parse("fe80::1234").GetAddressBytes(),
            3);
        var scopedRemote = new IPEndPoint(scopedAddress, 51_000);
        var scopedProof = CreateProof(fixture, scopedRemote);
        Check.True(
            fixture.Service.TryValidateClientProof(
                scopedProof,
                scopedRemote,
                new byte[16]),
            "IPv6 scoped endpoint verifies");
        var wrongScope = new IPEndPoint(
            new IPAddress(scopedAddress.GetAddressBytes(), 4),
            scopedRemote.Port);
        Check.True(
            !fixture.Service.TryValidateClientProof(
                scopedProof,
                wrongScope,
                new byte[16]),
            "IPv6 scope change rejects");

        using var expiry = CookieFixture.Create(
            lifetime: TimeSpan.FromSeconds(10),
            rotation: TimeSpan.FromSeconds(20));
        var expiringProof = CreateProof(expiry, Remote);
        expiry.Time.Advance(TimeSpan.FromSeconds(10));
        Check.True(
            expiry.Service.TryValidateClientProof(
                expiringProof,
                Remote,
                new byte[16]),
            "UDP cookie is valid at exact TTL boundary");
        expiry.Time.Advance(TimeSpan.FromSeconds(1));
        Check.True(
            !expiry.Service.TryValidateClientProof(
                expiringProof,
                Remote,
                new byte[16]),
            "UDP cookie expires after TTL boundary");

        var issuerTime = NewTime();
        var validatorTime = NewTime();
        using var issuer = CookieFixture.Create(
            timeProvider: issuerTime);
        using var validator = CookieFixture.Create(
            timeProvider: validatorTime);
        issuerTime.Advance(TimeSpan.FromSeconds(3));
        var futureProof = CreateProof(issuer, Remote);
        Check.True(
            !validator.Service.TryValidateClientProof(
                futureProof,
                Remote,
                new byte[16]),
            "cookie beyond future-skew allowance rejects");
        validatorTime.Advance(TimeSpan.FromSeconds(1));
        Check.True(
            validator.Service.TryValidateClientProof(
                futureProof,
                Remote,
                new byte[16]),
            "cookie at exact future-skew boundary verifies");
    }

    private static byte[] CreateProof(
        CookieFixture fixture,
        IPEndPoint endpoint)
    {
        var hello = EncodeHello(ConnectionId, Nonce);
        var challenge = new byte[128];
        var proof = new byte[128];
        Check.True(
            fixture.Service.TryCreateChallenge(
                hello,
                endpoint,
                challenge,
                out var challengeBytes) &&
            challengeBytes == challenge.Length,
            "UDP challenge fixture");
        Check.True(
            SecureUdpAddressValidation.TryCreateClientProof(
                challenge,
                proof,
                out var proofBytes) &&
            proofBytes == proof.Length,
            "UDP proof fixture");
        return proof;
    }

    private static byte[] EncodeHello(
        ReadOnlySpan<byte> connectionId,
        ReadOnlySpan<byte> nonce)
    {
        var hello = new byte[128];
        Check.True(
            SecureUdpAddressValidation.TryEncodeClientHello(
                connectionId,
                nonce,
                hello,
                out var written) &&
            written == hello.Length,
            "UDP hello fixture");
        return hello;
    }

    private static ManualTimeProvider NewTime()
    {
        var time = new ManualTimeProvider();
        time.Advance(TimeSpan.FromDays(20_000));
        return time;
    }

    private sealed class CookieFixture : IDisposable
    {
        private CookieFixture(
            ManualTimeProvider time,
            DeterministicKeyMaterial material,
            SecureUdpAddressValidation service)
        {
            Time = time;
            Material = material;
            Service = service;
        }

        public ManualTimeProvider Time { get; }

        public DeterministicKeyMaterial Material { get; }

        public SecureUdpAddressValidation Service { get; }

        public static CookieFixture Create(
            ManualTimeProvider? timeProvider = null,
            TimeSpan? lifetime = null,
            TimeSpan? rotation = null,
            string audience = "reborn-game",
            ushort udpPort = 7444,
            uint serverId = 100)
        {
            var time = timeProvider ?? NewTime();
            var policy = new SecureUdpCookiePolicy(
                lifetime ?? TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2),
                rotation ?? TimeSpan.FromSeconds(60));
            var material = new DeterministicKeyMaterial();
            var ring = new SecureUdpCookieKeyRing(
                time,
                policy.KeyRotation,
                material.CreateSecret,
                material.CreateKeyId);
            var protector = new SecureUdpCookieProtector(
                policy,
                serverId,
                udpPort,
                audience,
                time,
                ring);
            return new CookieFixture(
                time,
                material,
                new SecureUdpAddressValidation(1_200, protector));
        }

        public void Dispose()
        {
            Service.Dispose();
        }
    }

    private sealed class DeterministicKeyMaterial
    {
        private uint _nextKeyId = 0x01020304;
        private byte _nextSeed = 1;

        public List<byte[]> Secrets { get; } = [];

        public byte[] CreateSecret()
        {
            var seed = _nextSeed++;
            var secret = Enumerable.Range(0, 32)
                .Select(index => checked((byte)(seed + index)))
                .ToArray();
            Secrets.Add(secret);
            return secret;
        }

        public uint CreateKeyId()
        {
            var value = _nextKeyId;
            _nextKeyId = checked(_nextKeyId + 0x01010101);
            return value;
        }
    }
}
