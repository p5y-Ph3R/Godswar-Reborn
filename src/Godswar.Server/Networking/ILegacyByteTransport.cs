namespace Godswar.Server.Networking;

/// <summary>
/// Provides the ordered byte stream consumed by the legacy session protocol.
/// Packet framing and cipher state intentionally remain outside this transport.
/// </summary>
internal interface ILegacyByteTransport : IAsyncDisposable
{
    string RemoteEndPoint { get; }

    ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes every supplied byte in order and completes after the transport is flushed.
    /// </summary>
    ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken);

    void Disconnect();
}
