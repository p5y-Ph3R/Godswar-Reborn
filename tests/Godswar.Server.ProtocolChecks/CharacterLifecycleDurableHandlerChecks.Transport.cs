using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterLifecycleDurableHandlerChecks
{
    private interface ILifecycleCaptureTransport :
        ILegacyByteTransport
    {
        IReadOnlyList<SecureLegacyCommandResult> CommandResults
        { get; }
        IReadOnlyList<string> Events { get; }
        int DisconnectCount { get; }
        IReadOnlyList<byte[]> ReadClearPackets();
    }

    private abstract class LifecycleCaptureTransport :
        ILifecycleCaptureTransport
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _legacyWrites = [];
        private readonly List<SecureLegacyCommandResult> _results = [];
        private readonly List<string> _events = [];
        private int _disconnectCount;

        public abstract string RemoteEndPoint { get; }

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

        public int DisconnectCount =>
            Volatile.Read(ref _disconnectCount);

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

        public IReadOnlyList<byte[]> ReadClearPackets()
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
                if (encrypted.Length - offset < 4)
                {
                    throw new InvalidDataException(
                        "Lifecycle capture ended inside a frame.");
                }
                var length =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        encrypted.AsSpan(offset, 2));
                if (length < 4 ||
                    length > encrypted.Length - offset)
                {
                    throw new InvalidDataException(
                        "Lifecycle capture contains an invalid frame.");
                }
                packets.Add(
                    encrypted.AsSpan(offset, length).ToArray());
                offset += length;
            }
            return packets;
        }

        protected ValueTask RecordResultAsync(
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

        public void Disconnect() =>
            Interlocked.Increment(ref _disconnectCount);

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }

    private sealed class LifecycleRawTransport :
        LifecycleCaptureTransport
    {
        public override string RemoteEndPoint =>
            "raw-character-lifecycle-handler-check";
    }

    private sealed class LifecycleSecureTransport :
        LifecycleCaptureTransport,
        ISecureControlChannel,
        ISecureCommandResultTransport
    {
        public LifecycleSecureTransport()
        {
            var connectionId = Enumerable.Repeat(
                (byte)0x41,
                SecureProtocolConstants.ConnectionIdBytes).ToArray();
            var clientInstanceId = Enumerable.Repeat(
                (byte)0x52,
                SecureProtocolConstants.ClientInstanceIdBytes).ToArray();
            var originHash = Enumerable.Repeat(
                (byte)0x63,
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

        public override string RemoteEndPoint =>
            "secure-character-lifecycle-handler-check";
        public SecureConnectionContext ConnectionContext { get; }
        public SecureBoundGamePrincipal? BoundGamePrincipal => null;
        public bool SupportsRealtimeMovement => false;
        public bool IsRealtimeMovementActive => false;

        public ValueTask SendLegacyCommandResultAsync(
            SecureLegacyCommandResult result,
            CancellationToken cancellationToken) =>
            RecordResultAsync(result, cancellationToken);

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
                    "Lifecycle checks cannot issue game grants."));

        public void MarkAuthenticated()
        {
        }
    }
}
