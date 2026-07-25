using System.Net.Security;

namespace Godswar.Server.Networking.Secure;

internal static class SecureStreamIo
{
    public static async ValueTask ReadExactlyAsync(
        SslStream stream,
        Memory<byte> destination,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        NetworkTimeoutStage timeoutStage)
    {
        using var deadline = new CancellationTokenSource(
            timeout,
            timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
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
                        $"TLS peer closed after {offset} of {destination.Length} required bytes.");
                }

                offset += read;
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            throw new NetworkDeadlineException(timeoutStage);
        }
    }

    public static async ValueTask WriteExactlyAsync(
        SslStream stream,
        ReadOnlyMemory<byte> source,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        NetworkTimeoutStage timeoutStage)
    {
        using var deadline = new CancellationTokenSource(
            timeout,
            timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
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
            throw new NetworkDeadlineException(timeoutStage);
        }
    }
}
