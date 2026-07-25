using System.Net.Sockets;

namespace Godswar.Server.Networking;

internal interface ILegacyByteTransportFactory
{
    ValueTask<ILegacyByteTransport> CreateAsync(
        TcpClient client,
        NetworkEndpointRole endpointRole,
        long acceptedTimestamp,
        CancellationToken cancellationToken);
}

internal sealed class RawTcpLegacyTransportFactory :
    ILegacyByteTransportFactory
{
    public static RawTcpLegacyTransportFactory Instance { get; } = new();

    private RawTcpLegacyTransportFactory()
    {
    }

    public ValueTask<ILegacyByteTransport> CreateAsync(
        TcpClient client,
        NetworkEndpointRole endpointRole,
        long acceptedTimestamp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ILegacyByteTransport>(
            new RawTcpLegacyTransport(client));
    }
}
