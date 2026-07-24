using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Networking;

internal sealed class ClientSession : IAsyncDisposable
{
    private const int MaxPacketLength = 8196;

    private readonly ILegacyByteTransport _transport;
    private readonly PacketCipher _receiveCipher = new();
    private readonly PacketCipher _sendCipher = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ClientSession(ILegacyByteTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public string RemoteEndPoint => _transport.RemoteEndPoint;

    public void Disconnect()
    {
        _transport.Disconnect();
    }

    public async Task<GamePacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var header = await ReadExactlyOrNullAsync(2, cancellationToken);
        if (header is null)
        {
            return null;
        }

        _receiveCipher.Transform(header);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length < 4 || length > MaxPacketLength)
        {
            throw new InvalidDataException($"Invalid packet length {length}.");
        }

        var rest = await ReadExactlyOrNullAsync(length - 2, cancellationToken);
        if (rest is null)
        {
            return null;
        }

        _receiveCipher.Transform(rest);

        var packet = new byte[length];
        header.CopyTo(packet.AsSpan(0, 2));
        rest.CopyTo(packet.AsSpan(2));
        return new GamePacket(packet);
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> clearPacket,
        CancellationToken cancellationToken,
        string? label = null,
        bool framed = true)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(label))
            {
                LogSend(clearPacket, label, framed);
            }

            var encrypted = clearPacket.ToArray();
            _sendCipher.Transform(encrypted);
            await _transport.WriteAsync(encrypted, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void LogSend(ReadOnlyMemory<byte> clearPacketMemory, string label, bool framed)
    {
        var clearPacket = clearPacketMemory.Span;
        var previewLength = ShouldLogFullPacket(label)
            ? clearPacket.Length
            : Math.Min(clearPacket.Length, 32);
        var hexPreview = Convert.ToHexString(clearPacket[..previewLength]);

        if (clearPacket.Length < 4)
        {
            Console.WriteLine($"[net] send {label} to {RemoteEndPoint} actual={clearPacket.Length} hex={hexPreview}");
            return;
        }

        if (!framed)
        {
            Console.WriteLine($"[net] send {label} to {RemoteEndPoint} stream-chunk actual={clearPacket.Length} hex={hexPreview}");
            return;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(clearPacket[..2]);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(clearPacket.Slice(2, 2));
        var mismatch = framed && declaredLength != clearPacket.Length ? " declared/actual-mismatch" : string.Empty;
        Console.WriteLine(
            $"[net] send {label} to {RemoteEndPoint} opcode={opcode} declared={declaredLength} actual={clearPacket.Length}{mismatch} hex={hexPreview}");
    }

    private static bool ShouldLogFullPacket(string label)
    {
        return label.Contains("VisiblePlayer", StringComparison.Ordinal)
            || label.Contains("PlayerInspectEquipment", StringComparison.Ordinal)
            || label.Contains("PlayerInspectClear", StringComparison.Ordinal)
            || label.Contains("PlayerInspectVisual", StringComparison.Ordinal);
    }

    private async Task<byte[]?> ReadExactlyOrNullAsync(int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var read = await _transport.ReadAsync(
                buffer.AsMemory(offset, count - offset),
                cancellationToken);
            if (read == 0)
            {
                return offset == 0 ? null : throw new EndOfStreamException("Socket closed mid-packet.");
            }

            offset += read;
        }

        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        await _transport.DisposeAsync();
    }
}
