using System.Net.Sockets;

namespace Godswar.Server.Networking;

internal sealed class RawTcpLegacyTransport : ILegacyByteTransport
{
    private readonly TcpClient _client;
    private readonly object _disposeGate = new();
    private readonly NetworkStream _stream;
    private int _disconnectStarted;
    private Task? _disposeTask;

    public RawTcpLegacyTransport(TcpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        try
        {
            _client.NoDelay = true;
            RemoteEndPoint = _client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _stream = _client.GetStream();
        }
        catch
        {
            _client.Dispose();
            throw;
        }
    }

    public string RemoteEndPoint { get; }

    public ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        return _stream.ReadAsync(destination, cancellationToken);
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(source, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnectStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _client.Close();
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            disposeTask = _disposeTask ??= DisposeOwnedResourcesAsync();
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeOwnedResourcesAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        finally
        {
            _client.Dispose();
        }
    }
}
