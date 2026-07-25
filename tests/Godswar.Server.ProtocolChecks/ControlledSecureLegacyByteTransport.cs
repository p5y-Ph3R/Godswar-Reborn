using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal sealed class ControlledSecureLegacyByteTransport :
    ILegacyByteTransport,
    ISecureLegacyByteTransport
{
    private readonly ControlledLegacyByteTransport _inner = new();
    private int _authenticated;

    public string RemoteEndPoint => "secure";

    public int DisconnectCount => _inner.DisconnectCount;

    public bool IsAuthenticated =>
        Volatile.Read(ref _authenticated) != 0;

    public void QueueInbound(ReadOnlySpan<byte> bytes)
    {
        _inner.QueueInbound(bytes);
    }

    public Task WaitForReadCallsAsync(
        int expectedCount,
        TimeSpan? timeout = null)
    {
        return _inner.WaitForReadCallsAsync(expectedCount, timeout);
    }

    public ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        return _inner.ReadAsync(destination, cancellationToken);
    }

    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        return _inner.WriteAsync(source, cancellationToken);
    }

    public void MarkAuthenticated()
    {
        Volatile.Write(ref _authenticated, 1);
    }

    public void Disconnect()
    {
        _inner.Disconnect();
    }

    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }
}
