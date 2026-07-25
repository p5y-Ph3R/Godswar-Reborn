using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpEndpointServer
{
    private readonly SecureUdpBindingCoordinator _coordinator;
    private readonly string _host;
    private readonly SecureUdpRateLimiter _limiter;
    private readonly int _maximumDatagramBytes;
    private readonly SecureUdpSessionAuthority? _sessions;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource<IPEndPoint> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runStarted;

    public SecureUdpEndpointServer(
        string host,
        int port,
        int maximumDatagramBytes,
        SecureUdpBindingCoordinator coordinator,
        SecureUdpRateLimiter limiter,
        SecureUdpSessionAuthority? sessions = null,
        TimeProvider? timeProvider = null)
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
        _sessions = sessions;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
                if (received.ReceivedBytes > _maximumDatagramBytes)
                {
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome.Malformed);
                    continue;
                }

                responseBuffer.AsSpan().Clear();
                var dispatch = ProcessDatagram(
                    receiveBuffer.AsSpan(0, received.ReceivedBytes),
                    remote,
                    responseBuffer);
                SecureUdpMetrics.RecordOutcome(dispatch.Outcome);
                if (dispatch.ResponseBytes == 0)
                {
                    continue;
                }

                try
                {
                    var sent = await socket.SendToAsync(
                        responseBuffer.AsMemory(
                            0,
                            dispatch.ResponseBytes),
                        SocketFlags.None,
                        remote,
                        cancellationToken);
                    if (sent != dispatch.ResponseBytes)
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

    internal SecureUdpEndpointDispatch ProcessDatagram(
        ReadOnlySpan<byte> datagram,
        IPEndPoint remote,
        Span<byte> responseDestination)
    {
        if (SecureUdpProtectedCodec.TryDecodeHeader(
                datagram,
                out var protectedHeader))
        {
            if (!_limiter.TryAcquireProtectedCandidate(
                    remote.Address))
            {
                return Rejected(
                    SecureUdpDatagramOutcome.RateLimited);
            }
            if (_sessions is null)
            {
                return Rejected(
                    SecureUdpDatagramOutcome.ProtectedRejected);
            }

            return ProcessProtectedDatagram(
                protectedHeader.ConnectionId,
                datagram,
                remote,
                responseDestination);
        }

        var isBindingProofCandidate =
            SecureUdpBindingCodec.TryDecode(
                datagram,
                out var bindingCandidate) &&
            bindingCandidate.Type ==
                SecureUdpBindingType.AuthenticatedClientProof;
        var admitted = isBindingProofCandidate
            ? _limiter.TryAcquireBindingProof(remote.Address)
            : _limiter.TryAcquireUnvalidated(remote.Address);
        if (!admitted)
        {
            return Rejected(SecureUdpDatagramOutcome.RateLimited);
        }

        var result = _coordinator.ProcessDatagram(
            datagram,
            remote,
            responseDestination);
        var outcome = ToMetricOutcome(result);
        if (!result.HasResponse &&
            result.Principal is not null &&
            result.BindingRevision != 0 &&
            result.Outcome is
                SecureUdpBindingProcessOutcome.Bound or
                SecureUdpBindingProcessOutcome.AlreadyBound or
                SecureUdpBindingProcessOutcome.Rebound)
        {
            return CreateBindingConfirmation(
                datagram,
                remote,
                result,
                responseDestination,
                outcome);
        }

        return new SecureUdpEndpointDispatch(
            outcome,
            result.HasResponse ? result.ResponseBytes : 0);
    }

    private SecureUdpEndpointDispatch ProcessProtectedDatagram(
        SecureUdpConnectionKey connectionId,
        ReadOnlySpan<byte> datagram,
        IPEndPoint remote,
        Span<byte> responseDestination)
    {
        Span<byte> plaintext = stackalloc byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        try
        {
            var receiveUnixMilliseconds = GetUnixMilliseconds();
            var result = _sessions!.TryUnprotect(
                connectionId,
                remote,
                datagram,
                plaintext);
            if (!result.IsAccepted ||
                result.Header.MessageType !=
                    SecureUdpProtectedMessageType.Ping ||
                result.PayloadBytes !=
                    SecureUdpProtectedConstants.PingPayloadBytes)
            {
                return Rejected(
                    result.ProtectedError ==
                        SecureUdpProtectedError.ReplayRejected
                        ? SecureUdpDatagramOutcome.ReplayRejected
                        : SecureUdpDatagramOutcome.ProtectedRejected);
            }
            if (!_limiter.TryAcquireAuthenticatedSession(
                    connectionId))
            {
                return Rejected(
                    SecureUdpDatagramOutcome.RateLimited);
            }

            Span<byte> pong = stackalloc byte[
                SecureUdpProtectedConstants.PongPayloadBytes];
            plaintext[..SecureUdpProtectedConstants.PingPayloadBytes]
                .CopyTo(pong);
            BinaryPrimitives.WriteUInt64BigEndian(
                pong[16..],
                receiveUnixMilliseconds);
            BinaryPrimitives.WriteUInt64BigEndian(
                pong[24..],
                GetUnixMilliseconds());
            if (!_sessions.TryProtect(
                    connectionId,
                    remote,
                    result.BindingRevision,
                    SecureUdpProtectedMessageType.Pong,
                    pong,
                    responseDestination,
                    out var responseBytes,
                    out _,
                    out _) ||
                responseBytes <= 0 ||
                responseBytes >
                    SecureUdpProtectedConstants.MaximumDatagramBytes)
            {
                return Rejected(
                    SecureUdpDatagramOutcome.ProtectedRejected);
            }

            return new SecureUdpEndpointDispatch(
                SecureUdpDatagramOutcome.ProtectedPongSent,
                responseBytes);
        }
        finally
        {
            plaintext.Clear();
        }
    }

    private SecureUdpEndpointDispatch CreateBindingConfirmation(
        ReadOnlySpan<byte> datagram,
        IPEndPoint remote,
        SecureUdpBindingProcessResult binding,
        Span<byte> responseDestination,
        SecureUdpDatagramOutcome outcome)
    {
        if (_sessions is null ||
            !SecureUdpBindingCodec.TryDecode(
                datagram,
                out var proof) ||
            !SecureUdpConnectionKey.TryCreate(
                proof.ConnectionId,
                out var connectionId))
        {
            return Rejected(
                SecureUdpDatagramOutcome.ProtectedRejected);
        }

        Span<byte> payload = stackalloc byte[
            SecureUdpProtectedConstants.BindingConfirmPayloadBytes];
        proof.ClientNonce.CopyTo(payload);
        BinaryPrimitives.WriteUInt64BigEndian(
            payload[16..],
            binding.BindingRevision);
        BinaryPrimitives.WriteUInt64BigEndian(
            payload[24..],
            GetUnixMilliseconds());
        if (!_sessions.TryProtect(
                connectionId,
                remote,
                binding.BindingRevision,
                SecureUdpProtectedMessageType.BindingConfirm,
                payload,
                responseDestination,
                out var responseBytes,
                out _,
                out _) ||
            responseBytes <= 0 ||
            responseBytes > datagram.Length)
        {
            return Rejected(
                SecureUdpDatagramOutcome.ProtectedRejected);
        }

        return new SecureUdpEndpointDispatch(outcome, responseBytes);
    }

    private ulong GetUnixMilliseconds()
    {
        var value = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return checked((ulong)Math.Max(1, value));
    }

    private static SecureUdpEndpointDispatch Rejected(
        SecureUdpDatagramOutcome outcome)
    {
        return new SecureUdpEndpointDispatch(outcome, 0);
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
            SecureUdpBindingProcessOutcome.Rebound =>
                SecureUdpDatagramOutcome.Rebound,
            SecureUdpBindingProcessOutcome.ReplayRejected =>
                SecureUdpDatagramOutcome.ReplayRejected,
            SecureUdpBindingProcessOutcome.RebindRateLimited =>
                SecureUdpDatagramOutcome.RebindRateLimited,
            SecureUdpBindingProcessOutcome.Rejected or
            SecureUdpBindingProcessOutcome.InvalidEndpoint =>
                SecureUdpDatagramOutcome.Malformed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result))
        };
}

internal readonly record struct SecureUdpEndpointDispatch(
    SecureUdpDatagramOutcome Outcome,
    int ResponseBytes);
