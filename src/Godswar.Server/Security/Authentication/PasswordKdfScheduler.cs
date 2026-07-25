using System.Security.Cryptography;
using System.Threading.Channels;

namespace Godswar.Server.Security.Authentication;

internal interface IPasswordKdfScheduler : IAsyncDisposable
{
    ValueTask<byte[]> DeriveAsync(
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> salt,
        int iterations,
        CancellationToken cancellationToken);
}

internal interface IPasswordKeyDeriver
{
    byte[] Derive(
        byte[] password,
        byte[] salt,
        int iterations);
}

internal sealed class Pbkdf2Sha256KeyDeriver : IPasswordKeyDeriver
{
    public byte[] Derive(
        byte[] password,
        byte[] salt,
        int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            AuthenticationOptions.PasswordHashBytes);
    }
}

internal sealed class PasswordKdfAdmissionException : Exception
{
    public PasswordKdfAdmissionException()
        : base("Password-work admission reached its finite bound.")
    {
    }
}

internal sealed class PasswordKdfScheduler : IPasswordKdfScheduler
{
    private readonly AuthenticationPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly IPasswordKeyDeriver _keyDeriver;
    private readonly Channel<KdfWorkItem> _channel;
    private readonly SemaphoreSlim _slots;
    private readonly BoundedCredentialBudget _credentialBudget;
    private readonly Task[] _workers;
    private int _disposed;

    public PasswordKdfScheduler(
        AuthenticationOptions options,
        TimeProvider? timeProvider = null,
        IPasswordKeyDeriver? keyDeriver = null)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options)))
                .Snapshot(),
            timeProvider,
            keyDeriver)
    {
    }

    internal PasswordKdfScheduler(
        AuthenticationPolicy policy,
        TimeProvider? timeProvider = null,
        IPasswordKeyDeriver? keyDeriver = null)
    {
        _policy = policy;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _keyDeriver = keyDeriver ?? new Pbkdf2Sha256KeyDeriver();
        _slots = new SemaphoreSlim(
            policy.QueueCapacity,
            policy.QueueCapacity);
        _credentialBudget = new BoundedCredentialBudget(
            policy.QueueCredentialBytes);
        _channel = Channel.CreateBounded<KdfWorkItem>(
            new BoundedChannelOptions(policy.QueueCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = policy.MaximumConcurrentKdfs == 1,
                SingleWriter = false
            });
        _workers = Enumerable.Range(
                0,
                policy.MaximumConcurrentKdfs)
            .Select(_ => Task.Factory.StartNew(
                RunWorker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
    }

    internal int MaximumConcurrency => _policy.MaximumConcurrentKdfs;

    internal int QueuedOrActiveCredentialBytes =>
        _credentialBudget.AllocatedBytes;

    public async ValueTask<byte[]> DeriveAsync(
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> salt,
        int iterations,
        CancellationToken cancellationToken)
    {
        ValidateRequest(password, salt, iterations);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        using var admissionDeadline = new CancellationTokenSource(
            _policy.QueueAdmissionTimeout,
            _timeProvider);
        using var admissionLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                admissionDeadline.Token);
        var slotAcquired = false;
        var budgetAcquired = false;
        try
        {
            try
            {
                await _slots.WaitAsync(admissionLifetime.Token);
                slotAcquired = true;
                await _credentialBudget.AcquireAsync(
                    password.Length,
                    admissionLifetime.Token);
                budgetAcquired = true;
            }
            catch (OperationCanceledException)
                when (admissionDeadline.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
            {
                throw new PasswordKdfAdmissionException();
            }

            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            var work = new KdfWorkItem(
                password.Span,
                salt.Span,
                iterations);
            if (!_channel.Writer.TryWrite(work))
            {
                work.Dispose();
                throw new PasswordKdfAdmissionException();
            }

            slotAcquired = false;
            budgetAcquired = false;
            using var cancellationRegistration =
                cancellationToken.Register(
                    static state =>
                    {
                        var tuple =
                            ((KdfWorkItem Work, CancellationToken Token))state!;
                        tuple.Work.TryCancel(tuple.Token);
                    },
                    (work, cancellationToken));
            return await work.Completion.Task;
        }
        finally
        {
            if (budgetAcquired)
            {
                _credentialBudget.Release(password.Length);
            }
            if (slotAcquired)
            {
                _slots.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await Task.WhenAll(_workers);
        _slots.Dispose();
    }

    private void RunWorker()
    {
        while (true)
        {
            KdfWorkItem work;
            try
            {
                work = _channel.Reader.ReadAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (ChannelClosedException)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    work.TryFail(
                        new ObjectDisposedException(
                            nameof(PasswordKdfScheduler)));
                    continue;
                }
                if (work.Completion.Task.IsCompleted)
                {
                    continue;
                }

                var result = _keyDeriver.Derive(
                    work.Password,
                    work.Salt,
                    work.Iterations);
                if (!work.TryComplete(result))
                {
                    CryptographicOperations.ZeroMemory(result);
                }
            }
            catch (Exception error)
            {
                work.TryFail(error);
            }
            finally
            {
                var credentialBytes = work.Password.Length;
                work.Dispose();
                _credentialBudget.Release(credentialBytes);
                _slots.Release();
            }
        }
    }

    private void ValidateRequest(
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> salt,
        int iterations)
    {
        if (password.IsEmpty ||
            password.Length > AuthenticationOptions.MaximumPasswordBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(password),
                $"Password work accepts 1..{AuthenticationOptions.MaximumPasswordBytes} bytes.");
        }
        if (salt.Length != AuthenticationOptions.PasswordSaltBytes)
        {
            throw new ArgumentException(
                $"Password salt must be exactly {AuthenticationOptions.PasswordSaltBytes} bytes.",
                nameof(salt));
        }
        if (iterations < _policy.MinimumStoredIterations ||
            iterations > _policy.MaximumStoredIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                "Stored password cost is outside the configured safe range.");
        }
    }

    private sealed class KdfWorkItem : IDisposable
    {
        public KdfWorkItem(
            ReadOnlySpan<byte> password,
            ReadOnlySpan<byte> salt,
            int iterations)
        {
            Password = password.ToArray();
            Salt = salt.ToArray();
            Iterations = iterations;
        }

        public byte[] Password { get; }

        public byte[] Salt { get; }

        public int Iterations { get; }

        public TaskCompletionSource<byte[]> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryComplete(byte[] value) =>
            Completion.TrySetResult(value);

        public void TryCancel(CancellationToken cancellationToken) =>
            Completion.TrySetCanceled(cancellationToken);

        public void TryFail(Exception error) =>
            Completion.TrySetException(error);

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Password);
            CryptographicOperations.ZeroMemory(Salt);
        }
    }
}

internal sealed class BoundedCredentialBudget
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private int _allocatedBytes;

    public BoundedCredentialBudget(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int AllocatedBytes
    {
        get
        {
            lock (_gate)
            {
                return _allocatedBytes;
            }
        }
    }

    public async ValueTask AcquireAsync(
        int byteCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        if (byteCount > _capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCount),
                "One credential cannot exceed the complete byte budget.");
        }

        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_allocatedBytes <= _capacity - byteCount)
                {
                    _allocatedBytes += byteCount;
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    public void Release(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (byteCount > _allocatedBytes)
            {
                throw new InvalidOperationException(
                    "Credential byte accounting underflowed.");
            }

            _allocatedBytes -= byteCount;
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
