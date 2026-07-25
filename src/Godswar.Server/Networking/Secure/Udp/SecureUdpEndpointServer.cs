using System.Net;
using System.Net.Sockets;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpEndpointServer
{
    private readonly SecureUdpBindingCoordinator _coordinator;
    private readonly string _host;
    private readonly SecureUdpRateLimiter _limiter;
    private readonly int _maximumDatagramBytes;
    private readonly TaskCompletionSource<IPEndPoint> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runStarted;

    public SecureUdpEndpointServer(
        string host,
        int port,
        int maximumDatagramBytes,
        SecureUdpBindingCoordinator coordinator,
        SecureUdpRateLimiter limiter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (!IPAddress.TryParse(host, out _))
        {
            throw new ArgumentException(
                "The UDP listener host must be a literal IP address.",
                nameof(host));
        }
        if (port is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        if (maximumDatagramBytes is <
                SecureUdpBindingConstants.DatagramBytes or >
                SecureUdpBindingConstants.MaximumDatagramBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDatagramBytes));
        }

        _host = host;
        Port = port;
        _maximumDatagramBytes = maximumDatagramBytes;
        _coordinator = coordinator ??
            throw new ArgumentNullException(nameof(coordinator));
        _limiter = limiter ??
            throw new ArgumentNullException(nameof(limiter));
    }

    public int Port { get; }

    public Task<IPEndPoint> WaitUntilStartedAsync(
        CancellationToken cancellationToken = default)
    {
        return _started.Task.WaitAsync(cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "A UDP endpoint server instance can run only once.");
        }

        var address = IPAddress.Parse(_host);
        using var socket = new Socket(
            address.AddressFamily,
            SocketType.Dgram,
            ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(address, Port));
            var localEndpoint = (IPEndPoint)socket.LocalEndPoint!;
            _started.TrySetResult(localEndpoint);

            var receiveBuffer = new byte[_maximumDatagramBytes + 1];
            var responseBuffer = new byte[
                SecureUdpBindingConstants.DatagramBytes];
            EndPoint receiveEndpoint = new IPEndPoint(
                address.AddressFamily == AddressFamily.InterNetwork
                    ? IPAddress.Any
                    : IPAddress.IPv6Any,
                0);
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(
                        receiveBuffer,
                        SocketFlags.None,
                        receiveEndpoint,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException error)
                    when (error.SocketErrorCode is
                        SocketError.MessageSize or
                        SocketError.ConnectionReset)
                {
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome.Malformed);
                    continue;
                }

                SecureUdpMetrics.DatagramReceived(received.ReceivedBytes);
                if (received.RemoteEndPoint is not IPEndPoint remote)
                {
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome.Malformed);
                    continue;
                }
                if (!_limiter.TryAcquire(remote.Address))
                {
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome.RateLimited);
                    continue;
                }
                if (received.ReceivedBytes > _maximumDatagramBytes)
                {
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome.Malformed);
                    continue;
                }

                responseBuffer.AsSpan().Clear();
                var result = _coordinator.ProcessDatagram(
                    receiveBuffer.AsSpan(0, received.ReceivedBytes),
                    remote,
                    responseBuffer);
                SecureUdpMetrics.RecordOutcome(ToMetricOutcome(result));
                if (!result.HasResponse)
                {
                    continue;
                }

                try
                {
                    var sent = await socket.SendToAsync(
                        responseBuffer.AsMemory(0, result.ResponseBytes),
                        SocketFlags.None,
                        remote,
                        cancellationToken);
                    if (sent != result.ResponseBytes)
                    {
                        SecureUdpMetrics.RecordOutcome(
                            SecureUdpDatagramOutcome.TransportError);
                        continue;
                    }
                    SecureUdpMetrics.DatagramSent(sent);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException)
                {
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome.TransportError);
                }
            }
        }
        catch (Exception error)
        {
            _started.TrySetException(error);
            throw;
        }
    }

    private static SecureUdpDatagramOutcome ToMetricOutcome(
        SecureUdpBindingProcessResult result) =>
        result.Outcome switch
        {
            SecureUdpBindingProcessOutcome.ChallengeCreated =>
                SecureUdpDatagramOutcome.ChallengeSent,
            SecureUdpBindingProcessOutcome.Bound =>
                SecureUdpDatagramOutcome.Bound,
            SecureUdpBindingProcessOutcome.AlreadyBound =>
                SecureUdpDatagramOutcome.Idempotent,
            SecureUdpBindingProcessOutcome.UnknownSession =>
                SecureUdpDatagramOutcome.UnknownSession,
            SecureUdpBindingProcessOutcome.Expired =>
                SecureUdpDatagramOutcome.Expired,
            SecureUdpBindingProcessOutcome.InvalidProof =>
                SecureUdpDatagramOutcome.InvalidSessionProof,
            SecureUdpBindingProcessOutcome.EndpointConflict =>
                SecureUdpDatagramOutcome.EndpointConflict,
            SecureUdpBindingProcessOutcome.Rejected or
            SecureUdpBindingProcessOutcome.InvalidEndpoint =>
                SecureUdpDatagramOutcome.Malformed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result))
        };
}
