using System.Buffers;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransport
{
    private async Task RunReaderAsync()
    {
        var headerBytes = new byte[
            SecureProtocolConstants.FrameHeaderBytes];
        var operationMetadataAwaitingLegacyBytes = false;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var firstRead = await _stream.ReadAsync(
                    headerBytes.AsMemory(0, 1),
                    _lifetime.Token);
                if (firstRead == 0)
                {
                    if (operationMetadataAwaitingLegacyBytes)
                    {
                        throw new SecureTransportException(
                            "The secure channel closed with unassociated operation metadata.");
                    }
                    _ingress.Complete();
                    return;
                }

                await SecureStreamIo.ReadExactlyAsync(
                    _stream,
                    headerBytes.AsMemory(1),
                    _options.PacketHeaderTimeout,
                    _timeProvider,
                    _lifetime.Token,
                    NetworkTimeoutStage.SecureFrameHeader);
                if (!SecureFrameCodec.TryDecodeHeader(
                        headerBytes,
                        _secureRole,
                        SecureFrameDirection.ClientToServer,
                        _nextInboundSequence,
                        out var header))
                {
                    SecureNetworkMetrics.FrameCompleted(
                        _endpointRole,
                        SecureFrameOutcome.Malformed);
                    throw new SecureTransportException(
                        "The secure frame header is malformed, out of sequence, or invalid for this endpoint.");
                }
                if (!SecureFrameCodec.TryGetNextSequence(
                        _nextInboundSequence,
                        out var nextSequence))
                {
                    SecureNetworkMetrics.FrameCompleted(
                        _endpointRole,
                        SecureFrameOutcome.Malformed);
                    throw new SecureTransportException(
                        "The secure inbound sequence cannot wrap.");
                }

                var payloadLength = checked((int)header.PayloadLength);
                var payload = ArrayPool<byte>.Shared.Rent(
                    Math.Max(1, payloadLength));
                var payloadOwned = true;
                try
                {
                    await SecureStreamIo.ReadExactlyAsync(
                        _stream,
                        payload.AsMemory(0, payloadLength),
                        _options.PacketBodyTimeout,
                        _timeProvider,
                        _lifetime.Token,
                        NetworkTimeoutStage.SecureFrameBody);
                    _nextInboundSequence = nextSequence;

                    if (header.Type == SecureFrameType.Close)
                    {
                        if (operationMetadataAwaitingLegacyBytes)
                        {
                            throw new SecureTransportException(
                                "A close frame cannot abandon pending operation metadata.");
                        }
                        RecordValidReceivedFrame();
                        SecureNetworkMetrics.FrameCompleted(
                            _endpointRole,
                            SecureFrameOutcome.Accepted);
                        _ingress.Complete();
                        return;
                    }
                    if (header.Type == SecureFrameType.Pong)
                    {
                        await EnqueueControlAsync(
                            payload.AsMemory(0, payloadLength));
                        SecureNetworkMetrics.FrameCompleted(
                            _endpointRole,
                            SecureFrameOutcome.Accepted);
                        continue;
                    }
                    if (header.Type ==
                        SecureFrameType.RealtimeMovementInput)
                    {
                        var lease = Volatile.Read(
                            ref _udpRegistrationLease);
                        var movementResult = lease?.OfferTlsMovement(
                            payload.AsSpan(0, payloadLength));
                        if (movementResult is null ||
                            !movementResult.Value
                                .IsBenignProtocolResult)
                        {
                            var outcome = movementResult?.Status ==
                                Udp.SecureRealtimeMovementOfferStatus
                                    .Malformed
                                ? SecureFrameOutcome.Malformed
                                : SecureFrameOutcome.WrongPhase;
                            SecureNetworkMetrics.FrameCompleted(
                                _endpointRole,
                                outcome);
                            throw new SecureTransportException(
                                "A realtime movement frame was malformed or received without an active negotiated capability.");
                        }

                        RecordValidReceivedFrame();
                        SecureNetworkMetrics.FrameCompleted(
                            _endpointRole,
                            SecureFrameOutcome.Accepted);
                        continue;
                    }
                    if (header.Type ==
                        SecureFrameType.LegacyCommandOperation)
                    {
                        if (operationMetadataAwaitingLegacyBytes ||
                            !SecureLegacyCommandOperationCodec.TryDecode(
                                payload.AsSpan(0, payloadLength),
                                out var operation))
                        {
                            SecureNetworkMetrics.FrameCompleted(
                                _endpointRole,
                                SecureFrameOutcome.Malformed);
                            throw new SecureTransportException(
                                "Secure legacy operation metadata was malformed or duplicated.");
                        }

                        await EnqueueIngressAsync(
                            new SecureLegacyChunk(operation),
                            payloadLength);
                        operationMetadataAwaitingLegacyBytes = true;
                        RecordValidReceivedFrame();
                        SecureNetworkMetrics.FrameCompleted(
                            _endpointRole,
                            SecureFrameOutcome.Accepted);
                        continue;
                    }
                    if (header.Type != SecureFrameType.LegacyBytes)
                    {
                        SecureNetworkMetrics.FrameCompleted(
                            _endpointRole,
                            SecureFrameOutcome.WrongPhase);
                        throw new SecureTransportException(
                            "A control frame was received before its secure channel phase was active.");
                    }

                    var chunk = new SecureLegacyChunk(
                        payload,
                        payloadLength);
                    payloadOwned = false;
                    await EnqueueIngressAsync(chunk, payloadLength);

                    operationMetadataAwaitingLegacyBytes = false;
                    RecordValidReceivedFrame();
                    SecureNetworkMetrics.FrameCompleted(
                        _endpointRole,
                        SecureFrameOutcome.Accepted);
                }
                finally
                {
                    if (payloadOwned)
                    {
                        System.Security.Cryptography.CryptographicOperations
                            .ZeroMemory(payload.AsSpan(0, payloadLength));
                        ArrayPool<byte>.Shared.Return(payload);
                    }
                }
            }
        }
        catch (NetworkDeadlineException error)
        {
            var outcome = error.Stage is
                NetworkTimeoutStage.SecureFrameHeader or
                NetworkTimeoutStage.SecureFrameBody
                ? SecureFrameOutcome.DeadlineExceeded
                : SecureFrameOutcome.Malformed;
            SecureNetworkMetrics.FrameCompleted(_endpointRole, outcome);
            NetworkRuntimeMetrics.RecordTimeout(_endpointRole, error.Stage);
            Fail(error);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
            _ingress.Complete();
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                headerBytes);
        }
    }

    private async Task EnqueueIngressAsync(
        SecureLegacyChunk item,
        int byteCount)
    {
        using var deadline = new CancellationTokenSource(
            _options.QueueAdmissionTimeout,
            _timeProvider);
        using var admission =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                deadline.Token);
        try
        {
            await _ingress.EnqueueAsync(
                item,
                byteCount,
                admission.Token);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !_lifetime.IsCancellationRequested)
        {
            item.Return();
            SecureNetworkMetrics.FrameCompleted(
                _endpointRole,
                SecureFrameOutcome.QueueOverflow);
            throw new SecureIngressQueueOverflowException();
        }
        catch
        {
            item.Return();
            throw;
        }

        SecureNetworkMetrics.IngressEnqueued(
            _endpointRole,
            byteCount);
    }

    private async Task EnqueueControlAsync(
        ReadOnlyMemory<byte> payload)
    {
        var work = new SecureControlWork(payload.ToArray());
        using var deadline = new CancellationTokenSource(
            _options.QueueAdmissionTimeout,
            _timeProvider);
        using var admission =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                deadline.Token);
        try
        {
            await _controlQueue.EnqueueAsync(
                work,
                work.Payload.Length,
                admission.Token);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !_lifetime.IsCancellationRequested)
        {
            work.Clear();
            SecureNetworkMetrics.FrameCompleted(
                _endpointRole,
                SecureFrameOutcome.QueueOverflow);
            throw new SecureIngressQueueOverflowException();
        }
        catch
        {
            work.Clear();
            throw;
        }

        SecureNetworkMetrics.ControlEnqueued(
            _endpointRole,
            work.Payload.Length);
        await work.Completion.WaitAsync(_lifetime.Token);
    }
}
