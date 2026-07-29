using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private sealed class TransferCaptureTransport :
        ILegacyByteTransport,
        ISecureControlChannel,
        ISecureCommandResultTransport
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _legacyWrites = [];
        private readonly List<SecureLegacyCommandResult> _results = [];
        private readonly List<string> _events = [];

        public TransferCaptureTransport()
        {
            var connectionId = Enumerable.Repeat(
                (byte)0xA1,
                SecureProtocolConstants.ConnectionIdBytes).ToArray();
            var clientInstanceId = Enumerable.Repeat(
                (byte)0xB2,
                SecureProtocolConstants.ClientInstanceIdBytes).ToArray();
            var originHash = Enumerable.Repeat(
                (byte)0xC3,
                SecureProtocolConstants.BuildHashBytes).ToArray();
            try
            {
                ConnectionContext = new SecureConnectionContext(
                    SecureEndpointRole.Game,
                    SecureProtocolConstants.ProtocolMajor,
                    SecureProtocolConstants.ProtocolMinor,
                    connectionId,
                    clientInstanceId,
                    originHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(connectionId);
                CryptographicOperations.ZeroMemory(clientInstanceId);
                CryptographicOperations.ZeroMemory(originHash);
            }
        }

        public string RemoteEndPoint =>
            "secure-equipment-transfer-handler-check";
        public SecureConnectionContext ConnectionContext { get; }
        public SecureBoundGamePrincipal? BoundGamePrincipal => null;
        public bool SupportsRealtimeMovement => false;
        public bool IsRealtimeMovementActive => false;

        public IReadOnlyList<SecureLegacyCommandResult> CommandResults
        {
            get
            {
                lock (_gate)
                {
                    return _results.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

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
            lock (_gate)
            {
                _legacyWrites.Add(source.ToArray());
                _events.Add("legacy");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask SendLegacyCommandResultAsync(
            SecureLegacyCommandResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _results.Add(result);
                _events.Add("command-result");
            }
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<byte[]> ReadLegacyPackets()
        {
            byte[] encrypted;
            lock (_gate)
            {
                encrypted = _legacyWrites
                    .SelectMany(static value => value)
                    .ToArray();
            }
            new PacketCipher().Transform(encrypted);

            var packets = new List<byte[]>();
            var offset = 0;
            while (offset < encrypted.Length)
            {
                var length =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        encrypted.AsSpan(offset, 2));
                if (length < 4 ||
                    length > encrypted.Length - offset)
                {
                    throw new InvalidDataException(
                        "Captured transfer stream has an invalid frame.");
                }
                packets.Add(
                    encrypted.AsSpan(offset, length).ToArray());
                offset += length;
            }
            return packets;
        }

        public bool TryTakeRealtimeMovement(
            out SecureRealtimeMovementIngress ingress)
        {
            ingress = default;
            return false;
        }

        public bool TryPublishRealtimeSnapshot(
            in SecureRealtimePositionSnapshot snapshot) => false;

        public ValueTask SendGameGrantAsync(
            SecureGameGrant grant,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException(
                    "Transfer checks cannot issue login grants."));

        public void MarkAuthenticated()
        {
        }

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
