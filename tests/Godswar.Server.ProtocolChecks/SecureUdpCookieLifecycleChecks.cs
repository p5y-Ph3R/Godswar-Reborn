using System.Collections.Concurrent;
using System.Net;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureUdpCookieChecks
{
    private static void CheckRotationAndSecretLifecycle()
    {
        using var fixture = CookieFixture.Create(
            lifetime: TimeSpan.FromSeconds(10),
            rotation: TimeSpan.FromSeconds(20));
        fixture.Time.Advance(TimeSpan.FromSeconds(19));
        var proof = CreateProof(fixture, Remote);
        Check.True(
            SecureUdpBindingCodec.TryDecode(
                proof,
                out var beforeRotation),
            "pre-rotation proof decodes");
        var oldEpoch = beforeRotation.KeyEpoch;

        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Check.True(
            fixture.Service.TryValidateClientProof(
                proof,
                Remote,
                new byte[16]),
            "previous cookie key overlaps after rotation");
        var newProof = CreateProof(fixture, Remote);
        Check.True(
            SecureUdpBindingCodec.TryDecode(
                newProof,
                out var afterRotation) &&
            afterRotation.KeyEpoch != oldEpoch,
            "cookie key epoch rotates");

        var time = NewTime();
        var material = new DeterministicKeyMaterial();
        using var ring = new SecureUdpCookieKeyRing(
            time,
            TimeSpan.FromSeconds(20),
            material.CreateSecret,
            material.CreateKeyId);
        var first = ring.GetCurrentKeyId();
        time.Advance(TimeSpan.FromSeconds(20));
        var second = ring.GetCurrentKeyId();
        Span<byte> hash = stackalloc byte[32];
        Check.True(
            ring.TryComputeHash(first, "old"u8, hash),
            "previous cookie key remains available");
        time.Advance(TimeSpan.FromSeconds(40));
        var third = ring.GetCurrentKeyId();
        Check.True(
            third != first && third != second,
            "cookie epochs do not collide");
        Check.True(
            !ring.TryComputeHash(first, "old"u8, hash),
            "long-idle rotation rejects displaced previous key");
        Check.True(
            !ring.TryComputeHash(second, "old"u8, hash),
            "long-idle rotation rejects stale current key");
        Check.True(
            material.Secrets[0].All(static value => value == 0),
            "long-idle previous cookie secret is zeroed");
        Check.True(
            material.Secrets[1].All(static value => value == 0),
            "long-idle current cookie secret is zeroed");
        ring.Dispose();
        Check.True(
            material.Secrets.All(secret =>
                secret.All(static value => value == 0)),
            "all cookie secrets zero on disposal");

        var delayedTime = NewTime();
        var delayedMaterial = new DeterministicKeyMaterial();
        using var delayedRing = new SecureUdpCookieKeyRing(
            delayedTime,
            TimeSpan.FromSeconds(60),
            delayedMaterial.CreateSecret,
            delayedMaterial.CreateKeyId);
        var delayedFirst = delayedRing.GetCurrentKeyId();
        delayedTime.Advance(TimeSpan.FromSeconds(119));
        var delayedSecond = delayedRing.GetCurrentKeyId();
        Check.True(
            delayedSecond != delayedFirst &&
            delayedRing.TryComputeHash(
                delayedFirst,
                "delayed"u8,
                hash),
            "late first rotation preserves only scheduled overlap");
        delayedTime.Advance(TimeSpan.FromSeconds(2));
        Check.True(
            !delayedRing.TryComputeHash(
                delayedFirst,
                "expired"u8,
                hash),
            "fixed rotation schedule rejects stale key after 2R");
        Check.True(
            delayedMaterial.Secrets[0].All(
                static value => value == 0),
            "fixed rotation schedule zeroes stale key");
        delayedRing.Dispose();
        Check.True(
            delayedMaterial.Secrets.All(secret =>
                secret.All(static value => value == 0)),
            "delayed cookie secrets zero on disposal");
    }

    private static void CheckConcurrentValidation()
    {
        using var fixture = CookieFixture.Create();
        var failures = new ConcurrentQueue<int>();
        Parallel.For(0, 256, index =>
        {
            var nonce = Enumerable.Repeat(
                    checked((byte)(index % 254 + 1)),
                    16)
                .ToArray();
            var hello = EncodeHello(ConnectionId, nonce);
            var challenge = new byte[128];
            var proof = new byte[128];
            if (!fixture.Service.TryCreateChallenge(
                    hello,
                    Remote,
                    challenge,
                    out var challengeBytes) ||
                challengeBytes != 128 ||
                !SecureUdpAddressValidation.TryCreateClientProof(
                    challenge,
                    proof,
                    out var proofBytes) ||
                proofBytes != 128 ||
                !fixture.Service.TryValidateClientProof(
                    proof,
                    Remote,
                    new byte[16]))
            {
                failures.Enqueue(index);
            }
        });
        Check.Equal(0, failures.Count, "concurrent UDP cookie operations");
    }

    private static void CheckCookieAllocationBound()
    {
        using var fixture = CookieFixture.Create();
        var hello = EncodeHello(ConnectionId, Nonce);
        var challenge = new byte[128];
        var proof = new byte[128];
        var validated = new byte[16];
        var mappedRemote = new IPEndPoint(
            IPAddress.Parse("::ffff:203.0.113.9"),
            Remote.Port);
        for (var index = 0; index < 100; index++)
        {
            _ = fixture.Service.TryCreateChallenge(
                hello,
                Remote,
                challenge,
                out _);
            _ = SecureUdpAddressValidation.TryCreateClientProof(
                challenge,
                proof,
                out _);
            _ = fixture.Service.TryValidateClientProof(
                proof,
                Remote,
                validated);
        }

        var succeeded = true;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            succeeded &=
                fixture.Service.TryCreateChallenge(
                    hello,
                    Remote,
                    challenge,
                    out var challengeBytes) &&
                challengeBytes == challenge.Length &&
                SecureUdpAddressValidation.TryCreateClientProof(
                    challenge,
                    proof,
                    out var proofBytes) &&
                proofBytes == proof.Length &&
                fixture.Service.TryValidateClientProof(
                    proof,
                    Remote,
                    validated);
        }
        var allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        Check.True(succeeded, "warmed UDP cookie operations succeed");
        Check.Equal(
            0L,
            allocated,
            "warmed UDP cookie operation allocation bound");

        before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            succeeded &=
                fixture.Service.TryCreateChallenge(
                    hello,
                    mappedRemote,
                    challenge,
                    out var challengeBytes) &&
                challengeBytes == challenge.Length &&
                SecureUdpAddressValidation.TryCreateClientProof(
                    challenge,
                    proof,
                    out var proofBytes) &&
                proofBytes == proof.Length &&
                fixture.Service.TryValidateClientProof(
                    proof,
                    mappedRemote,
                    validated);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.True(
            succeeded,
            "warmed mapped-IPv4 UDP cookie operations succeed");
        Check.Equal(
            0L,
            allocated,
            "mapped-IPv4 UDP cookie operation allocation bound");
    }

    private static void CheckAdversarialInputsAndDisposal()
    {
        var fixture = CookieFixture.Create();
        var random = new Random(0x9A);
        var response = new byte[128];
        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var request = new byte[random.Next(0, 1_202)];
            random.NextBytes(request);
            var accepted = fixture.Service.TryCreateChallenge(
                request,
                Remote,
                response,
                out var responseBytes);
            Check.True(
                !accepted || responseBytes <= request.Length,
                "adversarial UDP response respects amplification bound");
        }

        fixture.Dispose();
        var hello = EncodeHello(ConnectionId, Nonce);
        Check.Throws<ObjectDisposedException>(
            () => fixture.Service.TryCreateChallenge(
                hello,
                Remote,
                response,
                out _),
            "disposed UDP cookie service");
    }
}
