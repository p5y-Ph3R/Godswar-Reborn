using System.Buffers;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransport
{
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        if (source.IsEmpty)
        {
            return;
        }

        using var writeLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
        await _writeGate.WaitAsync(writeLifetime.Token);
        try
        {
            var offset = 0;
            while (offset < source.Length)
            {
                var payloadLength = Math.Min(
                    SecureProtocolConstants.MaximumPayloadBytes,
                    source.Length - offset);
                await WriteOneFrameAsync(
                    SecureFrameType.LegacyBytes,
                    source.Slice(offset, payloadLength),
                    writeLifetime.Token);
                offset += payloadLength;
            }
        }
        catch (Exception error)
        {
            Fail(error);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteControlFrameAsync(
        SecureFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var writeLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
        await _writeGate.WaitAsync(writeLifetime.Token);
        try
        {
            await WriteOneFrameAsync(
                type,
                payload,
                writeLifetime.Token);
        }
        catch (Exception error)
        {
            Fail(error);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteOneFrameAsync(
        SecureFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!SecureFrameCodec.TryGetNextSequence(
                _nextOutboundSequence,
                out var nextSequence))
        {
            throw new SecureTransportException(
                "The secure outbound sequence cannot wrap.");
        }

        var frameLength =
            SecureProtocolConstants.FrameHeaderBytes +
            payload.Length;
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            if (!SecureFrameCodec.TryEncode(
                    new SecureFrameHeader(
                        checked((uint)payload.Length),
                        type,
                        _nextOutboundSequence),
                    payload.Span,
                    _secureRole,
                    SecureFrameDirection.ServerToClient,
                    frame.AsSpan(0, frameLength),
                    out var bytesWritten) ||
                bytesWritten != frameLength)
            {
                throw new InvalidOperationException(
                    "A bounded TLS frame could not be encoded.");
            }

            await _stream.WriteAsync(
                frame.AsMemory(0, frameLength),
                cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            _nextOutboundSequence = nextSequence;
            RecordValidSentFrame();
            SecureNetworkMetrics.FrameCompleted(
                _endpointRole,
                SecureFrameOutcome.Accepted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                frame.AsSpan(0, frameLength));
            ArrayPool<byte>.Shared.Return(frame);
        }
    }
}
