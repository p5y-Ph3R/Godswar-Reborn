using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal sealed class WarehouseCaptureTransport :
    ILegacyByteTransport,
    ISecureControlChannel,
    ISecureCommandResultTransport
{
    private readonly object _gate = new();
    private readonly MemoryStream _legacyWrites = new();
    private readonly List<SecureLegacyCommandResult> _results = [];
    private readonly List<string> _events = [];

    public WarehouseCaptureTransport()
    {
        var connectionId = Enumerable.Repeat(
            (byte)0xD1,
            SecureProtocolConstants.ConnectionIdBytes).ToArray();
        var clientInstanceId = Enumerable.Repeat(
            (byte)0xD2,
            SecureProtocolConstants.ClientInstanceIdBytes).ToArray();
        var originHash = Enumerable.Repeat(
            (byte)0xD3,
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

    public string RemoteEndPoint => "secure-warehouse-check";

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
            _legacyWrites.Write(source.Span);
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
            _events.Add("secure");
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<byte[]> ReadLegacyPackets()
    {
        byte[] clear;
        lock (_gate)
        {
            clear = _legacyWrites.ToArray();
        }
        new PacketCipher().Transform(clear);
        return SplitFrames(clear);
    }

    private static IReadOnlyList<byte[]> SplitFrames(byte[] clear)
    {
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < clear.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clear.AsSpan(offset, sizeof(ushort)));
            if (length < 4 || length > clear.Length - offset)
            {
                throw new InvalidDataException(
                    "Captured warehouse stream has an invalid frame.");
            }
            packets.Add(clear.AsSpan(offset, length).ToArray());
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
                "Warehouse checks cannot issue login grants."));

    public void MarkAuthenticated()
    {
    }

    public void Disconnect()
    {
    }

    public ValueTask DisposeAsync()
    {
        _legacyWrites.Dispose();
        return ValueTask.CompletedTask;
    }
}
