using System.Buffers.Binary;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureProtocolCodecChecks
{
    private static void CheckIncrementalReads()
    {
        var client = EncodeClientPreface();
        for (var length = 0; length < client.Length; length++)
        {
            var status = SecurePrefaceCodec.ReadClient(
                client.AsSpan(0, length),
                SecureEndpointRole.Login,
                out var preface,
                out var consumed);
            Check.True(
                status == SecureDecodeStatus.NeedMore,
                $"client preface split {length} needs more");
            Check.True(preface is null, "incomplete client preface is absent");
            Check.Equal(0, consumed, "incomplete client consumes nothing");
        }

        var coalescedClient = client.Concat(new byte[] { 1, 2, 3 }).ToArray();
        Check.True(
            SecurePrefaceCodec.ReadClient(
                coalescedClient,
                SecureEndpointRole.Login,
                out var decodedClient,
                out var clientConsumed) == SecureDecodeStatus.Done,
            "coalesced client preface completes");
        Check.True(decodedClient is not null, "coalesced client decode exists");
        Check.Equal(72, clientConsumed, "client read preserves remainder");

        var server = EncodeServerPreface();
        for (var length = 0; length < server.Length; length++)
        {
            var status = SecurePrefaceCodec.ReadServer(
                server.AsSpan(0, length),
                SecureEndpointRole.Login,
                out var preface,
                out var consumed);
            Check.True(
                status == SecureDecodeStatus.NeedMore,
                $"server preface split {length} needs more");
            Check.True(preface is null, "incomplete server preface is absent");
            Check.Equal(0, consumed, "incomplete server consumes nothing");
        }

        var coalescedServer = server.Concat(new byte[] { 4, 5 }).ToArray();
        Check.True(
            SecurePrefaceCodec.ReadServer(
                coalescedServer,
                SecureEndpointRole.Login,
                out var decodedServer,
                out var serverConsumed) == SecureDecodeStatus.Done,
            "coalesced server preface completes");
        Check.True(decodedServer is not null, "coalesced server decode exists");
        Check.Equal(40, serverConsumed, "server read preserves remainder");

        var payload = Convert.FromHexString("0102030405060708");
        var frame = EncodeFrame(
            new SecureFrameHeader(
                (uint)payload.Length,
                SecureFrameType.Ping,
                1),
            payload,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient);
        for (var length = 0; length < frame.Length; length++)
        {
            var status = SecureFrameCodec.Read(
                frame.AsSpan(0, length),
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                1,
                out var header,
                out var consumed);
            Check.True(
                status == SecureDecodeStatus.NeedMore,
                $"frame split {length} needs more");
            Check.Equal(default, header, "incomplete frame header is cleared");
            Check.Equal(0, consumed, "incomplete frame consumes nothing");
        }

        var coalescedFrame = frame.Concat(frame).ToArray();
        Check.True(
            SecureFrameCodec.Read(
                coalescedFrame,
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                1,
                out var firstHeader,
                out var frameConsumed) == SecureDecodeStatus.Done,
            "coalesced frame completes first record");
        Check.Equal(
            frame.Length,
            frameConsumed,
            "frame read preserves coalesced remainder");
        Check.Equal(8U, firstHeader.PayloadLength, "read frame payload length");

        var invalidFrame = (byte[])frame.Clone();
        invalidFrame[6] = 1;
        Check.True(
            SecureFrameCodec.Read(
                invalidFrame,
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                1,
                out _,
                out var invalidConsumed) == SecureDecodeStatus.Rejected,
            "complete malformed frame rejects");
        Check.Equal(0, invalidConsumed, "rejected frame consumes nothing");
    }

    private static void CheckFrameGoldenVector()
    {
        var payload = Convert.FromHexString("0102030405060708");
        var header = new SecureFrameHeader(
            (uint)payload.Length,
            SecureFrameType.Ping,
            1);
        var encoded = EncodeFrame(
            header,
            payload,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient);
        var expected = Convert.FromHexString(
            "00000008000100000000000000000001" +
            "0102030405060708");

        Check.True(
            encoded.SequenceEqual(expected),
            "frame golden bytes and network byte order");
        Check.True(
            SecureFrameCodec.TryDecode(
                encoded,
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                1,
                out var decodedHeader),
            "golden Ping frame decodes");
        Check.True(
            decodedHeader.Type == SecureFrameType.Ping,
            "decoded frame type");
        Check.Equal(1UL, decodedHeader.Sequence, "decoded frame sequence");
        Check.True(
            encoded.AsSpan(
                    SecureProtocolConstants.FrameHeaderBytes,
                    (int)decodedHeader.PayloadLength)
                .SequenceEqual(payload),
            "decoded frame payload");

        for (var length = 0; length < encoded.Length; length++)
        {
            Check.True(
                !SecureFrameCodec.TryDecode(
                    encoded.AsSpan(0, length),
                    SecureEndpointRole.Login,
                    SecureFrameDirection.ServerToClient,
                    1,
                    out _),
                $"frame truncation {length} rejects");
        }
        Check.True(
            !SecureFrameCodec.TryDecode(
                encoded.Concat(new byte[] { 0 }).ToArray(),
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                1,
                out _),
            "frame trailing byte rejects as one exact frame");
    }

    private static void CheckFrameBoundariesAndContext()
    {
        var header = new byte[SecureProtocolConstants.FrameHeaderBytes];
        foreach (var length in new uint[]
                 {
                     0, 1, 3, 4, 7, 8, 51, 52, 70, 71, 408, 409,
                     16_383, 16_384, 16_385, uint.MaxValue
                 })
        {
            header.AsSpan().Clear();
            BinaryPrimitives.WriteUInt32BigEndian(header, length);
            BinaryPrimitives.WriteUInt16BigEndian(
                header.AsSpan(4),
                (ushort)SecureFrameType.LegacyBytes);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), 1);
            var expected = length is >= 1 and <= 16_384;
            Check.Equal(
                expected,
                SecureFrameCodec.TryDecodeHeader(
                    header,
                    SecureEndpointRole.Login,
                    SecureFrameDirection.ClientToServer,
                    1,
                    out _),
                $"LegacyBytes payload boundary {length}");
        }

        CheckHeaderContext(
            SecureFrameType.Ping,
            8,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.Ping,
            8,
            SecureEndpointRole.Login,
            SecureFrameDirection.ClientToServer,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.Pong,
            8,
            SecureEndpointRole.Game,
            SecureFrameDirection.ClientToServer,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.Close,
            4,
            SecureEndpointRole.Login,
            SecureFrameDirection.ClientToServer,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.Close,
            4,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.GameGrant,
            71,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.GameGrant,
            71,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.GameBind,
            52,
            SecureEndpointRole.Game,
            SecureFrameDirection.ClientToServer,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.GameBind,
            52,
            SecureEndpointRole.Login,
            SecureFrameDirection.ClientToServer,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.BindResult,
            4,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.BindResult,
            4,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expected: false);

        header.AsSpan().Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, 8);
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(4),
            (ushort)SecureFrameType.Ping);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), 1);
        header[6] = 1;
        Check.True(
            !SecureFrameCodec.TryDecodeHeader(
                header,
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                1,
                out _),
            "nonzero frame flags reject");

        header[6] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 0xFFFF);
        Check.True(
            !SecureFrameCodec.TryDecodeHeader(
                header,
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                1,
                out _),
            "unknown frame type rejects");

        Check.True(
            !SecureFrameCodec.TryEncode(
                new SecureFrameHeader(
                    16_384,
                    SecureFrameType.LegacyBytes,
                    1),
                new byte[16_384],
                SecureEndpointRole.Login,
                SecureFrameDirection.ClientToServer,
                new byte[16_399],
                out var shortBytesWritten),
            "short frame destination rejects");
        Check.Equal(0, shortBytesWritten, "short frame writes zero bytes");

        CheckHeaderContext(
            SecureFrameType.Close,
            4,
            SecureEndpointRole.Login,
            (SecureFrameDirection)2,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.LegacyBytes,
            1,
            SecureEndpointRole.Game,
            (SecureFrameDirection)2,
            expected: false);
    }

    private static void CheckSequencePolicy()
    {
        var header = new byte[SecureProtocolConstants.FrameHeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, 8);
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(4),
            (ushort)SecureFrameType.Ping);

        foreach (var sequence in new[] { 0UL, 1UL, 2UL, ulong.MaxValue })
        {
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), sequence);
            var expected = sequence == 1;
            Check.Equal(
                expected,
                SecureFrameCodec.TryDecodeHeader(
                    header,
                    SecureEndpointRole.Login,
                    SecureFrameDirection.ServerToClient,
                    1,
                    out _),
                $"frame sequence {sequence} against expected one");
        }

        BinaryPrimitives.WriteUInt64BigEndian(
            header.AsSpan(8),
            ulong.MaxValue);
        Check.True(
            SecureFrameCodec.TryDecodeHeader(
                header,
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                ulong.MaxValue,
                out _),
            "maximum sequence can be the final accepted frame");
        Check.True(
            !SecureFrameCodec.TryGetNextSequence(0, out _),
            "zero sequence never advances");
        Check.True(
            SecureFrameCodec.TryGetNextSequence(
                ulong.MaxValue - 1,
                out var maximum),
            "penultimate sequence advances once");
        Check.Equal(ulong.MaxValue, maximum, "maximum sequence value");
        Check.True(
            !SecureFrameCodec.TryGetNextSequence(ulong.MaxValue, out _),
            "maximum sequence closes instead of wrapping");
    }

    private static void CheckFrameDecodeAllocationBound()
    {
        var payload = Convert.FromHexString("0102030405060708");
        var encoded = EncodeFrame(
            new SecureFrameHeader(
                (uint)payload.Length,
                SecureFrameType.Ping,
                1),
            payload,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient);

        SecureFrameCodec.TryDecode(
            encoded,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            1,
            out _);
        SecureFrameCodec.Read(
            encoded,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            1,
            out _,
            out _);

        var allSucceeded = true;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            allSucceeded &=
                SecureFrameCodec.TryDecode(
                    encoded,
                    SecureEndpointRole.Login,
                    SecureFrameDirection.ServerToClient,
                    1,
                    out _) &&
                SecureFrameCodec.Read(
                    encoded,
                    SecureEndpointRole.Login,
                    SecureFrameDirection.ServerToClient,
                    1,
                    out _,
                    out _) == SecureDecodeStatus.Done;
        }
        var allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            allocatedBefore;

        Check.True(allSucceeded, "allocation probe frames decode");
        Check.Equal(
            0L,
            allocated,
            "frame syntax decoding allocates no payload owner");
    }

    private static void CheckHeaderContext(
        SecureFrameType type,
        uint payloadLength,
        SecureEndpointRole role,
        SecureFrameDirection direction,
        bool expected)
    {
        var header = new byte[SecureProtocolConstants.FrameHeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, payloadLength);
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(4),
            (ushort)type);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), 1);

        Check.Equal(
            expected,
            SecureFrameCodec.TryDecodeHeader(
                header,
                role,
                direction,
                1,
                out _),
            $"{type} {role} {direction} context");
    }
}
