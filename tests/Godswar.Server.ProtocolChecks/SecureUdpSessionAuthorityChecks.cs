using System.Collections.Concurrent;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureUdpSessionAuthorityChecks
{
    private static readonly byte[] ClientInstanceId =
        Enumerable.Range(1, SecureProtocolConstants.ClientInstanceIdBytes)
            .Select(static value => checked((byte)value))
            .ToArray();

    private static readonly byte[] OriginSha256 =
        Enumerable.Range(1, SecureProtocolConstants.BuildHashBytes)
            .Select(static value => checked((byte)(value + 32)))
            .ToArray();

    public static Task RunAsync()
    {
        CheckConstructorAndKeyFactoryBounds();
        CheckCapacityAndDuplicateRegistration();
        CheckPendingExpiryAndGenerationSafeRelease();
        CheckConcurrentCapacity();
        CheckProofAuthenticationAndEndpointBinding();
        CheckRebindAndProtectedSessionContinuity();
        CheckKeepaliveActivityAndIdleCleanup();
        CheckOutboundTrafficDoesNotRefreshInboundLiveness();
        CheckUnknownRevokedAndExpiredProofs();
        CheckSecretZeroing();
        SecureUdpBindingCoordinatorChecks.Run();
        return Task.CompletedTask;
    }

    private static void CheckConstructorAndKeyFactoryBounds()
    {
        var time = CreateTime();
        Check.Throws<ArgumentOutOfRangeException>(
            () => new SecureUdpSessionAuthority(
                0,
                TimeSpan.FromSeconds(30),
                time),
            "zero UDP session capacity");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new SecureUdpSessionAuthority(
                1,
                TimeSpan.FromSeconds(4),
                time),
            "short UDP binding-offer lifetime");

        var rejectedKeys = new ConcurrentBag<byte[]>();
        using var authority = new SecureUdpSessionAuthority(
            1,
            TimeSpan.FromSeconds(30),
            time,
            () =>
            {
                var invalid = Enumerable.Repeat((byte)0xA5, 31).ToArray();
                rejectedKeys.Add(invalid);
                return invalid;
            });
        Check.Throws<CryptographicException>(
            () => authority.Register(
                CreateConnection(1),
                CreatePrincipal(1)),
            "repeated invalid UDP proof keys");
        Check.Equal(
            4,
            rejectedKeys.Count,
            "proof-key factory has a finite retry bound");
        Check.True(
            rejectedKeys.All(static key =>
                SecureUdpBindingCodec.IsAllZero(key)),
            "rejected proof-key material is zeroed");
        Check.Equal(
            0,
            authority.GetSnapshot().TrackedSessions,
            "failed proof-key generation reserves no session");
    }

    private static void CheckCapacityAndDuplicateRegistration()
    {
        var time = CreateTime();
        using var authority = CreateAuthority(1, time);
        var firstConnection = CreateConnection(1);
        var first = authority.Register(
            firstConnection,
            CreatePrincipal(1));
        Check.True(first.IsRegistered, "first UDP session registers");

        var duplicate = authority.Register(
            firstConnection,
            CreatePrincipal(2));
        Check.True(
            duplicate.Status ==
                SecureUdpSessionRegistrationStatus.DuplicateConnectionId &&
            duplicate.Lease is null,
            "duplicate TLS connection ID is rejected without a lease");

        var overflow = authority.Register(
            CreateConnection(2),
            CreatePrincipal(2));
        Check.True(
            overflow.Status ==
                SecureUdpSessionRegistrationStatus.CapacityExceeded &&
            overflow.Lease is null,
            "UDP session capacity rejects a distinct overflow");
        Check.Equal(
            new SecureUdpSessionAuthoritySnapshot(1, 1, 0),
            authority.GetSnapshot(),
            "capacity and duplicate rejection preserve one pending session");

        first.Lease!.Dispose();
        first.Lease.Dispose();
        Check.Equal(
            0,
            authority.GetSnapshot().TrackedSessions,
            "session lease release is idempotent");

        var afterRelease = authority.Register(
            CreateConnection(2),
            CreatePrincipal(2));
        Check.True(
            afterRelease.IsRegistered,
            "released capacity can be reused");
        afterRelease.Lease!.Dispose();
    }

    private static void CheckPendingExpiryAndGenerationSafeRelease()
    {
        var time = CreateTime();
        var keys = new ConcurrentQueue<byte[]>();
        using var authority = new SecureUdpSessionAuthority(
            1,
            TimeSpan.FromSeconds(5),
            time,
            () =>
            {
                var key = CreateProofKey(keys.Count + 1);
                keys.Enqueue(key);
                return key;
            });
        var connection = CreateConnection(9);
        var first = authority.Register(
            connection,
            CreatePrincipal(9));
        Check.True(first.IsRegistered, "expiring UDP offer registers");

        Span<byte> copiedConnection = stackalloc byte[
            SecureUdpBindingConstants.ConnectionIdBytes];
        Span<byte> copiedKey = stackalloc byte[
            SecureUdpTlsProofAuthenticator.KeyBytes];
        try
        {
            Check.True(
                first.Lease!.TryCopyGrantMaterial(
                    copiedConnection,
                    copiedKey,
                    out var expiry) &&
                copiedConnection.SequenceEqual(
                    connection.ConnectionId.Span) &&
                expiry == 6_000,
                "grant material carries the exact ID and monotonic expiry");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copiedConnection);
            CryptographicOperations.ZeroMemory(copiedKey);
        }

        time.Advance(TimeSpan.FromMilliseconds(4_999));
        Check.Equal(
            1,
            authority.GetSnapshot().PendingSessions,
            "pending offer remains before its exact deadline");
        time.Advance(TimeSpan.FromMilliseconds(1));
        Check.Equal(
            0,
            authority.GetSnapshot().TrackedSessions,
            "pending offer expires at its exact deadline");
        Check.True(
            keys.TryPeek(out var expiredKey) &&
            SecureUdpBindingCodec.IsAllZero(expiredKey),
            "expired pending offer zeroes its proof key");
        copiedConnection.Fill(0xCC);
        copiedKey.Fill(0xCC);
        Check.True(
            !first.Lease!.TryCopyGrantMaterial(
                copiedConnection,
                copiedKey,
                out _) &&
            SecureUdpBindingCodec.IsAllZero(copiedConnection) &&
            SecureUdpBindingCodec.IsAllZero(copiedKey),
            "expired lease cannot recover and clears grant output");

        var replacement = authority.Register(
            connection,
            CreatePrincipal(10));
        Check.True(
            replacement.IsRegistered,
            "expired connection ID can receive a new generation");
        first.Lease.Dispose();
        copiedConnection.Fill(0xCC);
        copiedKey.Fill(0xCC);
        Check.True(
            !first.Lease.TryCopyGrantMaterial(
                copiedConnection,
                copiedKey,
                out _) &&
            SecureUdpBindingCodec.IsAllZero(copiedConnection) &&
            SecureUdpBindingCodec.IsAllZero(copiedKey),
            "disposed lease clears stale grant output");
        Check.Equal(
            1,
            authority.GetSnapshot().PendingSessions,
            "late old-generation disposal preserves replacement session");
        Check.True(
            replacement.Lease!.TryCopyGrantMaterial(
                copiedConnection,
                copiedKey,
                out _),
            "replacement generation remains usable");
        replacement.Lease.Dispose();
    }

    private static void CheckConcurrentCapacity()
    {
        const int capacity = 8;
        var time = CreateTime();
        using var authority = CreateAuthority(capacity, time);
        var leases = new ConcurrentBag<SecureUdpSessionLease>();
        var unexpected = new ConcurrentQueue<string>();

        Parallel.For(
            1,
            257,
            value =>
            {
                var result = authority.Register(
                    CreateConnection(value),
                    CreatePrincipal(value));
                if (result.IsRegistered)
                {
                    leases.Add(result.Lease!);
                }
                else if (result.Status !=
                    SecureUdpSessionRegistrationStatus.CapacityExceeded)
                {
                    unexpected.Enqueue(result.Status.ToString());
                }
            });

        Check.Equal(
            capacity,
            leases.Count,
            "concurrent UDP registration never exceeds capacity");
        Check.Equal(
            0,
            unexpected.Count,
            "concurrent distinct IDs have only finite capacity rejection");
        Check.Equal(
            capacity,
            authority.GetSnapshot().TrackedSessions,
            "concurrent authority snapshot remains capacity-bounded");

        Parallel.ForEach(
            leases,
            lease => Parallel.Invoke(lease.Dispose, lease.Dispose));
        Check.Equal(
            0,
            authority.GetSnapshot().TrackedSessions,
            "concurrent repeated lease release returns all capacity");
    }

    private static ManualTimeProvider CreateTime()
    {
        var time = new ManualTimeProvider();
        time.Advance(TimeSpan.FromSeconds(1));
        return time;
    }

    private static SecureUdpSessionAuthority CreateAuthority(
        int capacity,
        ManualTimeProvider time)
    {
        var nextKey = 0;
        return new SecureUdpSessionAuthority(
            capacity,
            TimeSpan.FromSeconds(5),
            time,
            () => CreateProofKey(
                Interlocked.Increment(ref nextKey)));
    }

    private static SecureConnectionContext CreateConnection(int value)
    {
        var connectionId = CreateConnectionId(value);
        return new SecureConnectionContext(
            SecureEndpointRole.Game,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            connectionId,
            ClientInstanceId,
            OriginSha256);
    }

    private static SecureBoundGamePrincipal CreatePrincipal(int value)
    {
        return new SecureBoundGamePrincipal(
            value,
            $"udp-user-{value}",
            SecureGamePermissions.EnterWorld,
            Guid.NewGuid());
    }

    private static byte[] CreateConnectionId(int value)
    {
        var output = new byte[
            SecureUdpBindingConstants.ConnectionIdBytes];
        output[0] = 0xA5;
        output[12] = unchecked((byte)(value >> 24));
        output[13] = unchecked((byte)(value >> 16));
        output[14] = unchecked((byte)(value >> 8));
        output[15] = unchecked((byte)value);
        return output;
    }

    private static byte[] CreateProofKey(int value)
    {
        var output = new byte[SecureUdpTlsProofAuthenticator.KeyBytes];
        for (var index = 0; index < output.Length; index++)
        {
            output[index] = checked((byte)(index + 1));
        }
        output[^1] ^= unchecked((byte)value);
        if (SecureUdpBindingCodec.IsAllZero(output))
        {
            output[0] = 1;
        }
        return output;
    }

    internal static ManualTimeProvider CreateTestTime() =>
        CreateTime();

    internal static SecureUdpSessionAuthority CreateTestAuthority(
        int capacity,
        ManualTimeProvider time) =>
        CreateAuthority(capacity, time);

    internal static SecureConnectionContext CreateTestConnection(
        int value) =>
        CreateConnection(value);

    internal static SecureBoundGamePrincipal CreateTestPrincipal(
        int value) =>
        CreatePrincipal(value);

    internal static byte[] CreateTestProofKey(int value) =>
        CreateProofKey(value);
}
