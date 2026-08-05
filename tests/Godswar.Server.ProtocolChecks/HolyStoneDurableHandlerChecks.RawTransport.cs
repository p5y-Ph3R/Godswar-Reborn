using System.Buffers.Binary;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private sealed class RawHolyStoneCaptureTransport :
        ILegacyByteTransport
    {
        private readonly List<byte[]> _writes = [];

        public bool Disconnected { get; private set; }

        public string RemoteEndPoint => "raw-holy-stone-check";

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writes.Add(source.ToArray());
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<byte[]> ReadLegacyPackets()
        {
            var encrypted = _writes
                .SelectMany(static write => write)
                .ToArray();
            new PacketCipher().Transform(encrypted);

            var packets = new List<byte[]>();
            var offset = 0;
            while (offset < encrypted.Length)
            {
                if (encrypted.Length - offset < sizeof(uint))
                {
                    throw new InvalidDataException(
                        "Raw Holy Stone stream ends inside a header.");
                }
                var length = BinaryPrimitives.ReadUInt16LittleEndian(
                    encrypted.AsSpan(offset, sizeof(ushort)));
                if (length < sizeof(uint) ||
                    length > encrypted.Length - offset)
                {
                    throw new InvalidDataException(
                        "Raw Holy Stone frame length is invalid.");
                }
                packets.Add(
                    encrypted.AsSpan(offset, length).ToArray());
                offset += length;
            }
            return packets;
        }

        public void Disconnect()
        {
            Disconnected = true;
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
