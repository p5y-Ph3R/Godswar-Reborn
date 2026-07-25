using System.Net.Security;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransportFactory
{
    private async Task<SecureBoundGamePrincipal> BindGameAsync(
        SslStream sslStream,
        SecureConnectionContext connectionContext,
        IGameTicketStore ticketStore,
        SecureGameTarget expectedTarget,
        CancellationToken cancellationToken)
    {
        using var bindDeadline = new CancellationTokenSource(
            _options.GameBindTimeout,
            _timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            bindDeadline.Token);
        var headerBytes = new byte[SecureProtocolConstants.FrameHeaderBytes];
        var bindBytes = new byte[SecureProtocolConstants.GameBindBytes];
        try
        {
            try
            {
                await ReadExactlyUnderBindDeadlineAsync(
                    sslStream,
                    headerBytes,
                    lifetime.Token);
                if (!SecureFrameCodec.TryDecodeHeader(
                        headerBytes,
                        SecureEndpointRole.Game,
                        SecureFrameDirection.ClientToServer,
                        expectedSequence: 1,
                        out var header) ||
                    header.Type != SecureFrameType.GameBind)
                {
                    SecureNetworkMetrics.FrameCompleted(
                        NetworkEndpointRole.Game,
                        SecureFrameOutcome.WrongPhase);
                    throw new SecureTransportException(
                        "The first secure game frame must be a game-ticket bind.");
                }

                await ReadExactlyUnderBindDeadlineAsync(
                    sslStream,
                    bindBytes,
                    lifetime.Token);
            }
            catch (OperationCanceledException)
                when (bindDeadline.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
            {
                SecureNetworkMetrics.FrameCompleted(
                    NetworkEndpointRole.Game,
                    SecureFrameOutcome.DeadlineExceeded);
                NetworkRuntimeMetrics.RecordTimeout(
                    NetworkEndpointRole.Game,
                    NetworkTimeoutStage.GameBind);
                throw new NetworkDeadlineException(
                    NetworkTimeoutStage.GameBind);
            }

            if (!SecureGameControlCodec.TryDecodeBind(
                    bindBytes,
                    out var bind))
            {
                SecureNetworkMetrics.FrameCompleted(
                    NetworkEndpointRole.Game,
                    SecureFrameOutcome.Malformed);
                throw new SecureTransportException(
                    "The secure game bind payload is malformed.");
            }

            using (bind)
            {
                var consume = ticketStore.Consume(
                    bind!,
                    connectionContext,
                    expectedTarget);
                var bindStatus = consume.IsAccepted
                    ? SecureBindStatus.Accepted
                    : SecureBindStatus.Rejected;
                await WriteBindResultAsync(
                    sslStream,
                    bindStatus,
                    cancellationToken);
                SecureNetworkMetrics.FrameCompleted(
                    NetworkEndpointRole.Game,
                    consume.IsAccepted
                        ? SecureFrameOutcome.Accepted
                        : SecureFrameOutcome.Rejected);
                if (!consume.IsAccepted)
                {
                    throw new SecureTransportException(
                        "The secure game bind was rejected.");
                }

                return consume.Principal!;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(headerBytes);
            CryptographicOperations.ZeroMemory(bindBytes);
        }
    }

    private async Task WriteBindResultAsync(
        SslStream sslStream,
        SecureBindStatus status,
        CancellationToken cancellationToken)
    {
        var payload =
            new byte[SecureProtocolConstants.BindResultBytes];
        var response = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            SecureProtocolConstants.BindResultBytes];
        try
        {
            if (!SecureGameControlCodec.TryEncodeBindResult(
                    new SecureBindResult(status),
                    payload,
                    out var payloadLength) ||
                !SecureFrameCodec.TryEncode(
                    new SecureFrameHeader(
                        checked((uint)payloadLength),
                        SecureFrameType.BindResult,
                        Sequence: 1),
                    payload,
                    SecureEndpointRole.Game,
                    SecureFrameDirection.ServerToClient,
                    response,
                    out var responseLength) ||
                responseLength != response.Length)
            {
                throw new InvalidOperationException(
                    "The secure game bind result could not be encoded.");
            }

            await SecureStreamIo.WriteExactlyAsync(
                sslStream,
                response,
                _options.ReliableWriteTimeout,
                _timeProvider,
                cancellationToken,
                NetworkTimeoutStage.ReliableWrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(response);
        }
    }

    private async Task RejectGameUntilTicketSliceAsync(
        SslStream sslStream,
        CancellationToken cancellationToken)
    {
        using var bindDeadline = new CancellationTokenSource(
            _options.GameBindTimeout,
            _timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            bindDeadline.Token);
        var headerBytes = new byte[SecureProtocolConstants.FrameHeaderBytes];
        try
        {
            await ReadExactlyUnderBindDeadlineAsync(
                sslStream,
                headerBytes,
                lifetime.Token);
        }
        catch (OperationCanceledException)
            when (bindDeadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            NetworkRuntimeMetrics.RecordTimeout(
                NetworkEndpointRole.Game,
                NetworkTimeoutStage.GameBind);
            throw new NetworkDeadlineException(
                NetworkTimeoutStage.GameBind);
        }

        if (!SecureFrameCodec.TryDecodeHeader(
                headerBytes,
                SecureEndpointRole.Game,
                SecureFrameDirection.ClientToServer,
                expectedSequence: 1,
                out var header) ||
            header.Type != SecureFrameType.GameBind)
        {
            SecureNetworkMetrics.FrameCompleted(
                NetworkEndpointRole.Game,
                SecureFrameOutcome.WrongPhase);
            throw new SecureTransportException(
                "The first secure game frame must be a game-ticket bind.");
        }

        var bindBytes = new byte[SecureProtocolConstants.GameBindBytes];
        try
        {
            await ReadExactlyUnderBindDeadlineAsync(
                sslStream,
                bindBytes,
                lifetime.Token);
            if (!SecureGameControlCodec.TryDecodeBind(
                    bindBytes,
                    out var bind))
            {
                SecureNetworkMetrics.FrameCompleted(
                    NetworkEndpointRole.Game,
                    SecureFrameOutcome.Malformed);
                throw new SecureTransportException(
                    "The secure game bind payload is malformed.");
            }
            bind!.Dispose();
        }
        catch (OperationCanceledException)
            when (bindDeadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            NetworkRuntimeMetrics.RecordTimeout(
                NetworkEndpointRole.Game,
                NetworkTimeoutStage.GameBind);
            throw new NetworkDeadlineException(
                NetworkTimeoutStage.GameBind);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bindBytes);
        }

        SecureNetworkMetrics.FrameCompleted(
            NetworkEndpointRole.Game,
            SecureFrameOutcome.WrongPhase);
        var payload = new byte[SecureProtocolConstants.BindResultBytes];
        if (!SecureGameControlCodec.TryEncodeBindResult(
                new SecureBindResult(SecureBindStatus.PolicyRejected),
                payload,
                out _))
        {
            throw new InvalidOperationException(
                "The policy-rejected game bind result could not be encoded.");
        }

        var response = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            SecureProtocolConstants.BindResultBytes];
        if (!SecureFrameCodec.TryEncode(
                new SecureFrameHeader(
                    SecureProtocolConstants.BindResultBytes,
                    SecureFrameType.BindResult,
                    Sequence: 1),
                payload,
                SecureEndpointRole.Game,
                SecureFrameDirection.ServerToClient,
                response,
                out _))
        {
            throw new InvalidOperationException(
                "The policy-rejected game bind frame could not be encoded.");
        }

        await SecureStreamIo.WriteExactlyAsync(
            sslStream,
            response,
            _options.ReliableWriteTimeout,
            _timeProvider,
            cancellationToken,
            NetworkTimeoutStage.ReliableWrite);
    }

    private void RecordHandshakeBeforeAdmission(
        NetworkEndpointRole role,
        SecureHandshakeOutcome outcome,
        TimeSpan duration)
    {
        SecureNetworkMetrics.HandshakeRejectedBeforeAdmission(
            role,
            outcome,
            duration);
    }

    private static async ValueTask ReadExactlyUnderBindDeadlineAsync(
        SslStream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(
                destination[offset..],
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"TLS peer closed after {offset} of {destination.Length} game-bind bytes.");
            }

            offset += read;
        }
    }
}
