using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureProtocolCodecChecks
{
    private static void CheckSecretZeroization()
    {
        var grant = NewGrant();
        var grantIdBacking = GetSecretArray(grant, "_grantId");
        var grantTicketBacking = GetSecretArray(grant, "_ticket");
        Check.True(
            grantIdBacking.SequenceEqual(GrantId),
            "grant model owns its grant-ID copy");
        Check.True(
            grantTicketBacking.SequenceEqual(Ticket),
            "grant model owns its ticket copy");

        grant.Dispose();
        grant.Dispose();
        Check.True(grant.IsDisposed, "grant disposal is idempotent");
        Check.True(
            grantIdBacking.AsSpan().IndexOfAnyExcept((byte)0) < 0,
            "grant backing ID is zeroized");
        Check.True(
            grantTicketBacking.AsSpan().IndexOfAnyExcept((byte)0) < 0,
            "grant backing ticket is zeroized");
        AssertDisposedGrantRefusesEncoding(grant);

        var bind = new SecureGameBind(GrantId, Ticket);
        var bindGrantIdBacking = GetSecretArray(bind, "_grantId");
        var bindTicketBacking = GetSecretArray(bind, "_ticket");
        Check.True(
            bindGrantIdBacking.SequenceEqual(GrantId),
            "bind model owns its grant-ID copy");
        Check.True(
            bindTicketBacking.SequenceEqual(Ticket),
            "bind model owns its ticket copy");

        bind.Dispose();
        bind.Dispose();
        Check.True(bind.IsDisposed, "bind disposal is idempotent");
        Check.True(
            bindGrantIdBacking.AsSpan().IndexOfAnyExcept((byte)0) < 0,
            "bind backing ID is zeroized");
        Check.True(
            bindTicketBacking.AsSpan().IndexOfAnyExcept((byte)0) < 0,
            "bind backing ticket is zeroized");
        AssertDisposedBindRefusesEncoding(bind);
    }

    private static void CheckConcurrentSecretLifecycle()
    {
        var expectedGrant = EncodeMinimumGrant();
        var expectedBind = Convert.FromHexString(
            "01000000" +
            "0102030405060708090A0B0C0D0E0F10" +
            "202122232425262728292A2B2C2D2E2F" +
            "303132333435363738393A3B3C3D3E3F");

        for (var iteration = 0; iteration < 32; iteration++)
        {
            ForceConcurrentGrantEncodeAndDispose(expectedGrant);
            ForceConcurrentBindEncodeAndDispose(expectedBind);
        }

        CryptographicOperations.ZeroMemory(expectedGrant.AsSpan(36, 32));
        CryptographicOperations.ZeroMemory(expectedBind.AsSpan(20, 32));
    }

    private static void ForceConcurrentGrantEncodeAndDispose(
        ReadOnlySpan<byte> expected)
    {
        var grant = NewGrant();
        var secretLock = GetSecretLock(grant);
        var output = Enumerable.Repeat((byte)0xA5, 71).ToArray();
        using var ready = new CountdownEvent(2);
        var encoded = false;
        Exception? encoderFailure = null;
        Exception? disposerFailure = null;
        var encoder = new Thread(() =>
        {
            ready.Signal();
            try
            {
                encoded = SecureGameControlCodec.TryEncodeGrant(
                    grant,
                    output,
                    out _);
            }
            catch (Exception ex)
            {
                encoderFailure = ex;
            }
        })
        {
            IsBackground = true
        };
        var disposer = new Thread(() =>
        {
            ready.Signal();
            try
            {
                grant.Dispose();
            }
            catch (Exception ex)
            {
                disposerFailure = ex;
            }
        })
        {
            IsBackground = true
        };

        var workersReady = false;
        var contentionObserved = false;
        Monitor.Enter(secretLock);
        try
        {
            encoder.Start();
            disposer.Start();
            workersReady = ready.Wait(TimeSpan.FromSeconds(5));
            contentionObserved =
                workersReady &&
                WaitForMonitorContention(encoder, disposer);
            Monitor.Exit(secretLock);
        }
        finally
        {
            if (Monitor.IsEntered(secretLock))
            {
                Monitor.Exit(secretLock);
            }
        }
        var encoderCompleted = encoder.Join(TimeSpan.FromSeconds(5));
        var disposerCompleted = disposer.Join(TimeSpan.FromSeconds(5));
        Check.True(encoderCompleted, "grant encoder completes without deadlock");
        Check.True(
            disposerCompleted,
            "grant disposer completes without deadlock");
        Check.True(workersReady, "grant race workers start");
        Check.True(
            contentionObserved,
            "grant encode and dispose contend on the held lifecycle lock");
        Check.True(encoderFailure is null, "grant encoder does not throw");
        Check.True(disposerFailure is null, "grant disposer does not throw");

        Check.True(
            encoded
                ? output.AsSpan().SequenceEqual(expected)
                : output.AsSpan().IndexOfAnyExcept((byte)0) < 0,
            "contended grant encode is complete or cleared");
        AssertDisposedGrantRefusesEncoding(grant);
        grant.Dispose();
    }

    private static void ForceConcurrentBindEncodeAndDispose(
        ReadOnlySpan<byte> expected)
    {
        var bind = new SecureGameBind(GrantId, Ticket);
        var secretLock = GetSecretLock(bind);
        var output = Enumerable.Repeat((byte)0xA5, 52).ToArray();
        using var ready = new CountdownEvent(2);
        var encoded = false;
        Exception? encoderFailure = null;
        Exception? disposerFailure = null;
        var encoder = new Thread(() =>
        {
            ready.Signal();
            try
            {
                encoded = SecureGameControlCodec.TryEncodeBind(
                    bind,
                    output,
                    out _);
            }
            catch (Exception ex)
            {
                encoderFailure = ex;
            }
        })
        {
            IsBackground = true
        };
        var disposer = new Thread(() =>
        {
            ready.Signal();
            try
            {
                bind.Dispose();
            }
            catch (Exception ex)
            {
                disposerFailure = ex;
            }
        })
        {
            IsBackground = true
        };

        var workersReady = false;
        var contentionObserved = false;
        Monitor.Enter(secretLock);
        try
        {
            encoder.Start();
            disposer.Start();
            workersReady = ready.Wait(TimeSpan.FromSeconds(5));
            contentionObserved =
                workersReady &&
                WaitForMonitorContention(encoder, disposer);
            Monitor.Exit(secretLock);
        }
        finally
        {
            if (Monitor.IsEntered(secretLock))
            {
                Monitor.Exit(secretLock);
            }
        }
        var encoderCompleted = encoder.Join(TimeSpan.FromSeconds(5));
        var disposerCompleted = disposer.Join(TimeSpan.FromSeconds(5));
        Check.True(encoderCompleted, "bind encoder completes without deadlock");
        Check.True(
            disposerCompleted,
            "bind disposer completes without deadlock");
        Check.True(workersReady, "bind race workers start");
        Check.True(
            contentionObserved,
            "bind encode and dispose contend on the held lifecycle lock");
        Check.True(encoderFailure is null, "bind encoder does not throw");
        Check.True(disposerFailure is null, "bind disposer does not throw");

        Check.True(
            encoded
                ? output.AsSpan().SequenceEqual(expected)
                : output.AsSpan().IndexOfAnyExcept((byte)0) < 0,
            "contended bind encode is complete or cleared");
        AssertDisposedBindRefusesEncoding(bind);
        bind.Dispose();
    }

    private static void AssertDisposedGrantRefusesEncoding(
        SecureGameGrant grant)
    {
        Span<byte> grantId = stackalloc byte[16];
        Span<byte> ticket = stackalloc byte[32];
        Check.True(
            !grant.TryCopySecrets(grantId, ticket),
            "disposed grant refuses secret access");
        var output = Enumerable.Repeat((byte)0xA5, 71).ToArray();
        Check.True(
            !SecureGameControlCodec.TryEncodeGrant(
                grant,
                output,
                out var bytesWritten),
            "disposed grant cannot encode");
        Check.Equal(0, bytesWritten, "disposed grant writes zero bytes");
        Check.True(
            output.AsSpan().IndexOfAnyExcept((byte)0) < 0,
            "failed disposed-grant encode clears destination");
    }

    private static void AssertDisposedBindRefusesEncoding(
        SecureGameBind bind)
    {
        Span<byte> grantId = stackalloc byte[16];
        Span<byte> ticket = stackalloc byte[32];
        Check.True(
            !bind.TryCopySecrets(grantId, ticket),
            "disposed bind refuses secret access");
        var output = Enumerable.Repeat((byte)0xA5, 52).ToArray();
        Check.True(
            !SecureGameControlCodec.TryEncodeBind(
                bind,
                output,
                out var bytesWritten),
            "disposed bind cannot encode");
        Check.Equal(0, bytesWritten, "disposed bind writes zero bytes");
        Check.True(
            output.AsSpan().IndexOfAnyExcept((byte)0) < 0,
            "failed disposed-bind encode clears destination");
    }

    private static byte[] GetSecretArray(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Check.True(field is not null, $"{fieldName} backing field exists");
        var value = field!.GetValue(owner) as byte[];
        Check.True(value is not null, $"{fieldName} backing array exists");
        return value!;
    }

    private static object GetSecretLock(object owner)
    {
        var field = owner.GetType().GetField(
            "_secretLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Check.True(field is not null, "secret lifecycle lock exists");
        var value = field!.GetValue(owner);
        Check.True(value is not null, "secret lifecycle lock is initialized");
        return value!;
    }

    private static bool WaitForMonitorContention(
        Thread first,
        Thread second)
    {
        return SpinWait.SpinUntil(
            () => IsWaiting(first) && IsWaiting(second),
            TimeSpan.FromSeconds(5));
    }

    private static bool IsWaiting(Thread thread)
    {
        return (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
    }
}
