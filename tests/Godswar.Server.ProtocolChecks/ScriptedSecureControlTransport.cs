using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.ProtocolChecks;

internal sealed class ScriptedSecureControlTransport :
    ILegacyByteTransport,
    ISecureControlChannel
{
    private readonly byte[] _inbound;
    private readonly MemoryStream _legacyWrites = new();
    private readonly object _gate = new();
    private SecureGameGrant? _capturedGrant;
    private int _inboundOffset;
    private int _authenticated;
    private int _disconnected;
    private int _disposed;

    public ScriptedSecureControlTransport(
        SecureConnectionContext context,
        byte[] inbound,
        SecureBoundGamePrincipal? boundGamePrincipal = null)
    {
        ConnectionContext = context ??
            throw new ArgumentNullException(nameof(context));
        _inbound = inbound ??
            throw new ArgumentNullException(nameof(inbound));
        BoundGamePrincipal = boundGamePrincipal;
    }

    public string RemoteEndPoint => "secure";

    public SecureConnectionContext ConnectionContext { get; }

    public SecureBoundGamePrincipal? BoundGamePrincipal { get; }

    public bool IsAuthenticated =>
        Volatile.Read(ref _authenticated) != 0;

    public int DisconnectCount =>
        Volatile.Read(ref _disconnected);

    public Action? AfterGameGrantWrite { get; set; }

    public Action? BeforeLegacyWrite { get; set; }

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

    private readonly List<string> _events = [];

    public byte[] LegacyWrites
    {
        get
        {
            lock (_gate)
            {
                return _legacyWrites.ToArray();
            }
        }
    }

    public ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disconnected) != 0 ||
            _inboundOffset >= _inbound.Length)
        {
            return ValueTask.FromResult(0);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var count = Math.Min(
            destination.Length,
            _inbound.Length - _inboundOffset);
        _inbound.AsMemory(_inboundOffset, count).CopyTo(destination);
        _inboundOffset += count;
        return ValueTask.FromResult(count);
    }

    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeLegacyWrite?.Invoke();
        lock (_gate)
        {
            _events.Add("legacy");
            _legacyWrites.Write(source.Span);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask SendGameGrantAsync(
        SecureGameGrant grant,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(grant);
        if (!IsAuthenticated ||
            ConnectionContext.Role != SecureEndpointRole.Login ||
            BoundGamePrincipal is not null)
        {
            throw new InvalidOperationException(
                "The scripted transport enforces authenticated login grant order.");
        }

        var grantId = new byte[SecureProtocolConstants.GrantIdBytes];
        var ticket = new byte[SecureProtocolConstants.TicketBytes];
        try
        {
            if (!grant.TryCopySecrets(grantId, ticket))
            {
                throw new InvalidOperationException(
                    "The scripted transport could not copy grant secrets.");
            }

            var copy = new SecureGameGrant(
                grant.RouteHost,
                grant.TlsHost,
                grant.Audience,
                grant.RoutePort,
                grant.TlsPort,
                grant.TargetServerId,
                grant.ExpiryUnixMilliseconds,
                grantId,
                ticket);
            lock (_gate)
            {
                if (_capturedGrant is not null)
                {
                    copy.Dispose();
                    throw new InvalidOperationException(
                        "The scripted login channel received a duplicate grant.");
                }

                _capturedGrant = copy;
                _events.Add("grant");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantId);
            CryptographicOperations.ZeroMemory(ticket);
        }

        AfterGameGrantWrite?.Invoke();
        return ValueTask.CompletedTask;
    }

    public SecureGameGrant TakeGrant()
    {
        lock (_gate)
        {
            var grant = _capturedGrant ??
                throw new InvalidOperationException(
                    "No game grant was captured.");
            _capturedGrant = null;
            return grant;
        }
    }

    public void MarkAuthenticated()
    {
        Volatile.Write(ref _authenticated, 1);
    }

    public void Disconnect()
    {
        Interlocked.Exchange(ref _disconnected, 1);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Disconnect();
            lock (_gate)
            {
                _capturedGrant?.Dispose();
                _capturedGrant = null;
                _legacyWrites.Dispose();
            }
            CryptographicOperations.ZeroMemory(_inbound);
        }

        return ValueTask.CompletedTask;
    }

    public bool SupportsRealtimeMovement => false;

    public bool IsRealtimeMovementActive => false;

    public bool TryTakeRealtimeMovement(
        out SecureRealtimeMovementIngress ingress)
    {
        ingress = default;
        return false;
    }

    public bool TryPublishRealtimeSnapshot(
        in SecureRealtimePositionSnapshot snapshot) => false;
}
