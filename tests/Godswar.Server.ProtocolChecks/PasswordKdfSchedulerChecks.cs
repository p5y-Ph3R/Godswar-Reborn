using System.Collections.Concurrent;
using System.Security.Cryptography;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PasswordKdfSchedulerChecks
{
    public static async Task RunAsync()
    {
        await CheckConcurrencyAndByteBoundsAsync();
        await CheckFiniteAdmissionAsync();
        await CheckCancellationAndZeroingAsync();
        await CheckServiceBusyAndDeadlineOutcomesAsync();
    }

    private static async Task CheckConcurrencyAndByteBoundsAsync()
    {
        using var release = new ManualResetEventSlim();
        var deriver = new BlockingKeyDeriver(release);
        var options = TestOptions(
            concurrency: 2,
            capacity: 4,
            credentialBytes: 64,
            admissionMilliseconds: 500);
        await using var scheduler = new PasswordKdfScheduler(
            options,
            keyDeriver: deriver);
        var passwords = Enumerable.Range(1, 4)
            .Select(value => Enumerable.Repeat((byte)value, 8).ToArray())
            .ToArray();
        var salt = Enumerable.Range(0, 16)
            .Select(static value => (byte)value)
            .ToArray();
        var tasks = passwords
            .Select(password => scheduler.DeriveAsync(
                    password,
                    salt,
                    100_000,
                    CancellationToken.None)
                .AsTask())
            .ToArray();
        try
        {
            Check.True(
                SpinWait.SpinUntil(
                    () => deriver.CallCount >= 2,
                    TimeSpan.FromSeconds(2)),
                "fixed KDF workers begin admitted work");
            Check.True(
                SpinWait.SpinUntil(
                    () => scheduler.QueuedOrActiveCredentialBytes == 32,
                    TimeSpan.FromSeconds(2)),
                "active and queued credentials share one byte budget");
            Check.Equal(
                2,
                deriver.MaximumActive,
                "KDF work never exceeds configured concurrency");
            Check.Equal(
                2,
                scheduler.MaximumConcurrency,
                "scheduler exposes its finite worker bound");

            release.Set();
            var outputs = await Task.WhenAll(tasks);
            foreach (var output in outputs)
            {
                Check.Equal(
                    AuthenticationOptions.PasswordHashBytes,
                    output.Length,
                    "KDF worker output length");
                CryptographicOperations.ZeroMemory(output);
            }
            Check.True(
                SpinWait.SpinUntil(
                    () => scheduler.QueuedOrActiveCredentialBytes == 0,
                    TimeSpan.FromSeconds(2)),
                "credential byte accounting returns to zero");
            Check.True(
                deriver.PasswordBuffers.All(IsZero),
                "worker-owned password copies are zeroed");
        }
        finally
        {
            release.Set();
            await IgnoreFailuresAsync(tasks);
            foreach (var password in passwords)
            {
                CryptographicOperations.ZeroMemory(password);
            }
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static async Task CheckFiniteAdmissionAsync()
    {
        using var release = new ManualResetEventSlim();
        var deriver = new BlockingKeyDeriver(release);
        var options = TestOptions(
            concurrency: 1,
            capacity: 1,
            credentialBytes: 32,
            admissionMilliseconds: 25);
        await using var scheduler = new PasswordKdfScheduler(
            options,
            keyDeriver: deriver);
        var password = Enumerable.Repeat((byte)'a', 8).ToArray();
        var salt = new byte[16];
        var first = scheduler.DeriveAsync(
                password,
                salt,
                100_000,
                CancellationToken.None)
            .AsTask();
        try
        {
            Check.True(
                SpinWait.SpinUntil(
                    () => deriver.CallCount == 1,
                    TimeSpan.FromSeconds(2)),
                "first KDF occupies the sole bounded slot");
            var rejected = false;
            try
            {
                _ = await scheduler.DeriveAsync(
                    password,
                    salt,
                    100_000,
                    CancellationToken.None);
            }
            catch (PasswordKdfAdmissionException)
            {
                rejected = true;
            }

            Check.True(
                rejected,
                "full KDF scheduler rejects after finite admission wait");
        }
        finally
        {
            release.Set();
            if (await CompleteOrNullAsync(first) is { } output)
            {
                CryptographicOperations.ZeroMemory(output);
            }
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static async Task CheckCancellationAndZeroingAsync()
    {
        using var release = new ManualResetEventSlim();
        var deriver = new BlockingKeyDeriver(release);
        var options = TestOptions(
            concurrency: 1,
            capacity: 2,
            credentialBytes: 64,
            admissionMilliseconds: 500);
        await using var scheduler = new PasswordKdfScheduler(
            options,
            keyDeriver: deriver);
        var firstPassword = Enumerable.Repeat((byte)'x', 12).ToArray();
        var cancelledPassword = Enumerable.Repeat((byte)'y', 12).ToArray();
        var salt = new byte[16];
        var first = scheduler.DeriveAsync(
                firstPassword,
                salt,
                100_000,
                CancellationToken.None)
            .AsTask();
        using var cancelledLifetime = new CancellationTokenSource();
        var cancelled = scheduler.DeriveAsync(
                cancelledPassword,
                salt,
                100_000,
                cancelledLifetime.Token)
            .AsTask();
        try
        {
            Check.True(
                SpinWait.SpinUntil(
                    () => scheduler.QueuedOrActiveCredentialBytes == 24,
                    TimeSpan.FromSeconds(2)),
                "queued cancellation remains inside byte bound");
            cancelledLifetime.Cancel();
            var observedCancellation = false;
            try
            {
                _ = await cancelled;
            }
            catch (OperationCanceledException)
            {
                observedCancellation = true;
            }

            Check.True(
                observedCancellation,
                "caller cancellation completes without waiting for active KDF");
        }
        finally
        {
            release.Set();
            if (await CompleteOrNullAsync(first) is { } output)
            {
                CryptographicOperations.ZeroMemory(output);
            }
            await IgnoreFailuresAsync([cancelled]);
            Check.True(
                SpinWait.SpinUntil(
                    () => scheduler.QueuedOrActiveCredentialBytes == 0,
                    TimeSpan.FromSeconds(2)),
                "cancelled queued password is eventually removed");
            Check.Equal(
                1,
                deriver.CallCount,
                "cancelled queued work never enters the KDF");
            Check.True(
                deriver.PasswordBuffers.All(IsZero),
                "completed active password copy is zeroed");
            CryptographicOperations.ZeroMemory(firstPassword);
            CryptographicOperations.ZeroMemory(cancelledPassword);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static async Task CheckServiceBusyAndDeadlineOutcomesAsync()
    {
        var options = TestOptions(
            concurrency: 1,
            capacity: 1,
            credentialBytes: 32,
            admissionMilliseconds: 25);
        var password = "bounded-password"u8.ToArray();
        try
        {
            await using (var busy = new AccountAuthenticationService(
                new MissingAccountStore(),
                options,
                scheduler: new RejectingScheduler()))
            {
                var result = await busy.AuthenticateAsync(
                    "bounded-user",
                    password);
                Check.Equal(
                    (int)AccountAuthenticationStatus.Busy,
                    (int)result.Status,
                    "KDF admission overflow becomes a finite busy result");
            }

            var time = new ManualTimeProvider();
            var blockingScheduler = new CancellationOnlyScheduler();
            await using var timed = new AccountAuthenticationService(
                new MissingAccountStore(),
                options,
                time,
                blockingScheduler);
            var authentication = timed.AuthenticateAsync(
                "bounded-user",
                password);
            Check.True(
                SpinWait.SpinUntil(
                    () => blockingScheduler.Started,
                    TimeSpan.FromSeconds(2)),
                "deadline check reaches bounded KDF work");
            time.Advance(options.Snapshot().OperationTimeout);
            var timedOut = await authentication;
            Check.Equal(
                (int)AccountAuthenticationStatus.TimedOut,
                (int)timedOut.Status,
                "absolute operation deadline returns finite timeout");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private static AuthenticationOptions TestOptions(
        int concurrency,
        int capacity,
        int credentialBytes,
        int admissionMilliseconds)
    {
        return new AuthenticationOptions
        {
            Iterations = 100_000,
            MinimumStoredIterations = 100_000,
            MaximumStoredIterations = 200_000,
            MaximumConcurrentKdfs = concurrency,
            QueueCapacity = capacity,
            QueueCredentialBytes = credentialBytes,
            QueueAdmissionTimeoutMilliseconds = admissionMilliseconds,
            OperationTimeoutMilliseconds = 2_000
        };
    }

    private static bool IsZero(byte[] bytes) =>
        bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0;

    private static async Task<byte[]?> CompleteOrNullAsync(
        Task<byte[]> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return null;
        }
    }

    private static async Task IgnoreFailuresAsync(
        IEnumerable<Task<byte[]>> tasks)
    {
        foreach (var task in tasks)
        {
            if (await CompleteOrNullAsync(task) is { } output)
            {
                CryptographicOperations.ZeroMemory(output);
            }
        }
    }

    private sealed class BlockingKeyDeriver(
        ManualResetEventSlim release) : IPasswordKeyDeriver
    {
        private int _active;
        private int _callCount;
        private int _maximumActive;

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public ConcurrentBag<byte[]> PasswordBuffers { get; } = [];

        public byte[] Derive(
            byte[] password,
            byte[] salt,
            int iterations)
        {
            PasswordBuffers.Add(password);
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Test KDF release was not signalled.");
                }

                var output = new byte[
                    AuthenticationOptions.PasswordHashBytes];
                output.AsSpan().Fill(password[0]);
                return output;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActive);
                if (candidate <= current ||
                    Interlocked.CompareExchange(
                        ref _maximumActive,
                        candidate,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class MissingAccountStore : GameStoreTestStub
    {
        public override Task<StoredAccountCredential?>
            FindAccountCredentialAsync(
                string username,
                CancellationToken cancellationToken = default)
        {
            return Task.FromResult<StoredAccountCredential?>(null);
        }
    }

    private sealed class RejectingScheduler : IPasswordKdfScheduler
    {
        public ValueTask<byte[]> DeriveAsync(
            ReadOnlyMemory<byte> password,
            ReadOnlyMemory<byte> salt,
            int iterations,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromException<byte[]>(
                new PasswordKdfAdmissionException());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellationOnlyScheduler :
        IPasswordKdfScheduler
    {
        private int _started;

        public bool Started => Volatile.Read(ref _started) != 0;

        public async ValueTask<byte[]> DeriveAsync(
            ReadOnlyMemory<byte> password,
            ReadOnlyMemory<byte> salt,
            int iterations,
            CancellationToken cancellationToken)
        {
            Volatile.Write(ref _started, 1);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException(
                "Infinite test delay unexpectedly completed.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
