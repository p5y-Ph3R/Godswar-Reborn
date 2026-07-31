using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.B18CSmoke;

internal sealed class LegacySmokePeer : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly PacketCipher _receiveCipher = new();
    private readonly PacketCipher _sendCipher = new();
    private readonly NetworkStream _stream;

    private LegacySmokePeer(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<LegacySmokePeer> ConnectAsync(
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(AddressFamily.InterNetwork)
        {
            NoDelay = true
        };
        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback,
                port,
                cancellationToken);
            return new LegacySmokePeer(client);
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
                "The relay returned an invalid legacy packet length.");
        }

        var packet = new byte[length];
        header.CopyTo(packet, 0);
        await ReadExactlyAsync(packet.AsMemory(2), cancellationToken);
        _receiveCipher.Transform(packet.AsSpan(2));
        return packet;
    }

    public async Task WaitForRemoteCloseAsync(
        CancellationToken cancellationToken)
    {
        int received;
        try
        {
            received = await _stream.ReadAsync(
                new byte[1],
                cancellationToken);
        }
        catch (IOException)
        {
            // A reset also proves the worker-side connection was torn down.
            return;
        }
        catch (SocketException)
        {
            // Some platforms surface the reset directly from the socket.
            return;
        }

        if (received != 0)
        {
            throw new InvalidDataException(
                "The active relayed game connection emitted unexpected bytes.");
        }
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
                    "The relay closed before the expected packet completed.");
            }

            offset += read;
        }
    }
}
