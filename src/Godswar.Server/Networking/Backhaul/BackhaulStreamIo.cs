using System.Net.Security;
using System.Security.Authentication;

namespace Godswar.Server.Networking.Backhaul;

internal static class BackhaulStreamIo
{
    public static async ValueTask ReadExactlyAsync(
        SslStream stream,
        Memory<byte> destination,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        BackhaulTimeoutStage timeoutStage)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var deadline = new CancellationTokenSource(
            timeout,
            timeProvider);
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        var offset = 0;
        try
        {
            while (offset < destination.Length)
            {
                var read = await stream.ReadAsync(
                    destination[offset..],
                    lifetime.Token);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Backhaul peer closed after {offset} of " +
                        $"{destination.Length} required bytes.");
                }

                offset += read;
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            throw new BackhaulTimeoutException(timeoutStage);
        }
    }

    public static async ValueTask WriteExactlyAsync(
        SslStream stream,
        ReadOnlyMemory<byte> source,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        BackhaulTimeoutStage timeoutStage)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var deadline = new CancellationTokenSource(
            timeout,
            timeProvider);
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        try
        {
            await stream.WriteAsync(source, lifetime.Token);
            await stream.FlushAsync(lifetime.Token);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            throw new BackhaulTimeoutException(timeoutStage);
        }
    }

    public static async Task AuthenticateAsGatewayAsync(
        SslStream stream,
        SslClientAuthenticationOptions options,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        await RunHandshakeAsync(
            token => stream.AuthenticateAsClientAsync(
                options,
                token),
            timeout,
            timeProvider,
            cancellationToken);
        if (!BackhaulTlsPolicy.IsNegotiationAccepted(
                stream,
                localIsServer: false))
        {
            throw new AuthenticationException(
                "The gateway-to-worker TLS negotiation did not satisfy " +
                "the backhaul policy.");
        }
    }

    public static async Task AuthenticateAsWorkerAsync(
        SslStream stream,
        SslServerAuthenticationOptions options,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        await RunHandshakeAsync(
            token => stream.AuthenticateAsServerAsync(
                options,
                token),
            timeout,
            timeProvider,
            cancellationToken);
        if (!BackhaulTlsPolicy.IsNegotiationAccepted(
                stream,
                localIsServer: true))
        {
            throw new AuthenticationException(
                "The worker-side TLS negotiation did not satisfy the " +
                "backhaul policy.");
        }
    }

    private static async Task RunHandshakeAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(
            timeout,
            timeProvider);
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        try
        {
            await operation(lifetime.Token);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            throw new BackhaulTimeoutException(
                BackhaulTimeoutStage.TlsHandshake);
        }
    }
}

/// <summary>
/// Bounds expensive concurrent TLS negotiations independently of accepted
/// TCP connections.
/// </summary>
internal sealed class BackhaulHandshakeGate : IDisposable
{
    public const int MaximumConcurrency = 256;
    private readonly SemaphoreSlim _semaphore;
    private int _disposed;

    public BackhaulHandshakeGate(int concurrency)
    {
        if (concurrency is < 1 or > MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(concurrency),
                $"Backhaul handshake concurrency must be between 1 and " +
                $"{MaximumConcurrency}.");
        }

        Capacity = concurrency;
        _semaphore = new SemaphoreSlim(concurrency, concurrency);
    }

    public int Capacity { get; }

    public int Available => _semaphore.CurrentCount;

    public async ValueTask<IDisposable> AcquireAsync(
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        using var deadline = new CancellationTokenSource(
            timeout,
            timeProvider);
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        try
        {
            await _semaphore.WaitAsync(lifetime.Token);
            return new Lease(this);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            throw new BackhaulTimeoutException(
                BackhaulTimeoutStage.TlsHandshake);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    private void Release()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _semaphore.Release();
        }
    }

    private sealed class Lease(
        BackhaulHandshakeGate owner) : IDisposable
    {
        private BackhaulHandshakeGate? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
