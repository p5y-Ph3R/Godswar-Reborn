using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.LegacyLoginProbe;

internal sealed class LegacyProbePeer : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly PacketCipher _receiveCipher = new();
    private readonly PacketCipher _sendCipher = new();
    private readonly NetworkStream _stream;

    private LegacyProbePeer(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<LegacyProbePeer> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(AddressFamily.InterNetwork)
        {
            NoDelay = true
        };
        try
        {
            await client.ConnectAsync(address, port, cancellationToken);
            return new LegacyProbePeer(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> clearPacket,
        CancellationToken cancellationToken)
    {
        var encrypted = clearPacket.ToArray();
        try
        {
            _sendCipher.Transform(encrypted);
            await _stream.WriteAsync(encrypted, cancellationToken);
        }
        finally
        {
            Array.Clear(encrypted);
        }
    }

    public async Task<byte[]> ReadAsync(
        CancellationToken cancellationToken)
    {
        var header = new byte[2];
        await ReadExactlyAsync(header, cancellationToken);
        _receiveCipher.Transform(header);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length is < 4 or > LegacyProtocolLimits.MaxPacketLength)
        {
            throw new InvalidDataException(
                $"Server returned invalid legacy packet length {length}.");
        }

        var packet = new byte[length];
        header.CopyTo(packet, 0);
        await ReadExactlyAsync(packet.AsMemory(2), cancellationToken);
        _receiveCipher.Transform(packet.AsSpan(2));
        return packet;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await _stream.ReadAsync(
                destination[offset..],
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Server closed the legacy stream during a packet.");
            }

            offset += read;
        }
    }
}
