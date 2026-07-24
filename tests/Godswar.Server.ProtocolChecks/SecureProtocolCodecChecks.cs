using System.Buffers.Binary;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureProtocolCodecChecks
{
    private static readonly byte[] ClientInstanceId =
        Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();

    private static readonly byte[] OriginHash =
        Enumerable.Range(0x10, 32).Select(static value => (byte)value).ToArray();

    private static readonly byte[] ConnectionId =
        Enumerable.Range(0xA0, 16).Select(static value => (byte)value).ToArray();

    private static readonly byte[] GrantId =
        Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();

    private static readonly byte[] Ticket =
        Enumerable.Range(0x20, 32).Select(static value => (byte)value).ToArray();

    public static Task RunAsync()
    {
        CheckClientPrefaceGoldenVector();
        CheckServerPrefaceGoldenVector();
        CheckPrefaceRejections();
        CheckIncrementalReads();
        CheckFrameGoldenVector();
        CheckFrameBoundariesAndContext();
        CheckSequencePolicy();
        CheckFrameDecodeAllocationBound();
        CheckGameControlGoldenVectors();
        CheckGameControlBoundaries();
        CheckSecretZeroization();
        CheckConcurrentSecretLifecycle();
        CheckBoundedAdversarialInputs();
        return Task.CompletedTask;
    }

    private static void CheckClientPrefaceGoldenVector()
    {
        var preface = new SecureClientPreface(
            SecureEndpointRole.Login,
            ClientInstanceId,
            OriginHash);
        var encoded = new byte[SecureProtocolConstants.ClientPrefaceBytes];

        Check.True(
            SecurePrefaceCodec.TryEncodeClient(
                preface,
                encoded,
                out var bytesWritten),
            "client preface encodes");
        Check.Equal(72, bytesWritten, "client preface encoded length");

        var expected = Convert.FromHexString(
            "475753430048000100000000010000000000000000004000" +
            "000102030405060708090A0B0C0D0E0F" +
            "101112131415161718191A1B1C1D1E1F" +
            "202122232425262728292A2B2C2D2E2F");
        Check.True(
            encoded.SequenceEqual(expected),
            "client preface golden bytes and network byte order");

        Check.True(
            SecurePrefaceCodec.TryDecodeClient(
                encoded,
                SecureEndpointRole.Login,
                out var decoded),
            "client preface decodes");
        Check.True(decoded is not null, "decoded client preface exists");
        Check.True(
            decoded!.Role == SecureEndpointRole.Login,
            "decoded client role");
        Check.True(
            decoded.ClientInstanceId.Span.SequenceEqual(ClientInstanceId),
            "client instance ID round trips");
        Check.True(
            decoded.OriginSha256.Span.SequenceEqual(OriginHash),
            "Origin hash round trips");

        var game = new SecureClientPreface(
            SecureEndpointRole.Game,
            ClientInstanceId,
            OriginHash);
        Check.True(
            SecurePrefaceCodec.TryEncodeClient(
                game,
                encoded,
                out bytesWritten),
            "game client preface encodes");
        Check.Equal((byte)2, encoded[12], "game endpoint role byte");
        Check.True(
            SecurePrefaceCodec.TryDecodeClient(
                encoded,
                SecureEndpointRole.Game,
                out _),
            "game client preface decodes on game endpoint");
        Check.True(
            !SecurePrefaceCodec.TryDecodeClient(
                encoded,
                SecureEndpointRole.Login,
                out _),
            "game preface fails on login endpoint");
    }

    private static void CheckServerPrefaceGoldenVector()
    {
        var preface = new SecureServerPreface(
            SecureServerPrefaceStatus.Ok,
            SecureEndpointRole.Login,
            ConnectionId);
        var encoded = new byte[SecureProtocolConstants.ServerPrefaceBytes];

        Check.True(
            SecurePrefaceCodec.TryEncodeServer(
                preface,
                encoded,
                out var bytesWritten),
            "server preface encodes");
        Check.Equal(40, bytesWritten, "server preface encoded length");

        var expected = Convert.FromHexString(
            "4757535300280001000000010000000000004000001E005A" +
            "A0A1A2A3A4A5A6A7A8A9AAABACADAEAF");
        Check.True(
            encoded.SequenceEqual(expected),
            "server preface golden bytes and network byte order");
        Check.True(
            SecurePrefaceCodec.TryDecodeServer(
                encoded,
                SecureEndpointRole.Login,
                out var decoded),
            "server preface decodes");
        Check.True(decoded is not null, "decoded server preface exists");
        Check.True(
            decoded!.Status == SecureServerPrefaceStatus.Ok,
            "server status round trips");
        Check.True(
            decoded.ConnectionId.Span.SequenceEqual(ConnectionId),
            "connection ID round trips");

        var rejected = new SecureServerPreface(
            SecureServerPrefaceStatus.UnsupportedBuild,
            SecureEndpointRole.Game,
            new byte[16]);
        Check.True(
            SecurePrefaceCodec.TryEncodeServer(
                rejected,
                encoded,
                out bytesWritten),
            "rejection preface encodes");
        Check.Equal((byte)3, encoded[10], "rejection status byte");
        Check.Equal((byte)2, encoded[11], "rejection role byte");
        Check.True(
            encoded.AsSpan(24, 16).IndexOfAnyExcept((byte)0) < 0,
            "rejection connection ID is zero");
        Check.True(
            SecurePrefaceCodec.TryDecodeServer(
                encoded,
                SecureEndpointRole.Game,
                out _),
            "canonical rejection preface decodes");
    }

    private static void CheckPrefaceRejections()
    {
        var client = EncodeClientPreface();
        for (var length = 0; length < client.Length; length++)
        {
            Check.True(
                !SecurePrefaceCodec.TryDecodeClient(
                    client.AsSpan(0, length),
                    SecureEndpointRole.Login,
                    out _),
                $"client preface truncation {length} rejects");
        }
        Check.True(
            !SecurePrefaceCodec.TryDecodeClient(
                client.Concat(new byte[] { 0 }).ToArray(),
                SecureEndpointRole.Login,
                out _),
            "client preface trailing byte rejects");

        foreach (var offset in new[] { 0, 4, 6, 8, 10, 13, 14, 16, 20 })
        {
            var mutated = (byte[])client.Clone();
            mutated[offset] ^= 0x01;
            Check.True(
                !SecurePrefaceCodec.TryDecodeClient(
                    mutated,
                    SecureEndpointRole.Login,
                    out _),
                $"client preface field mutation at {offset} rejects");
        }
        var invalidRole = (byte[])client.Clone();
        invalidRole[12] = 3;
        Check.True(
            !SecurePrefaceCodec.TryDecodeClient(
                invalidRole,
                SecureEndpointRole.Login,
                out _),
            "unknown client role rejects");
        var zeroInstance = (byte[])client.Clone();
        zeroInstance.AsSpan(24, 16).Clear();
        Check.True(
            !SecurePrefaceCodec.TryDecodeClient(
                zeroInstance,
                SecureEndpointRole.Login,
                out _),
            "zero client-instance ID rejects");
        Check.Throws<ArgumentException>(
            () => new SecureClientPreface(
                SecureEndpointRole.Login,
                new byte[16],
                OriginHash),
            "client model rejects zero instance ID");
        var generatedOne = SecureClientPreface.Create(
            SecureEndpointRole.Login,
            OriginHash);
        var generatedTwo = SecureClientPreface.Create(
            SecureEndpointRole.Login,
            OriginHash);
        Check.True(
            generatedOne.ClientInstanceId.Span.IndexOfAnyExcept((byte)0) >= 0,
            "CSPRNG client-instance ID is nonzero");
        Check.True(
            !generatedOne.ClientInstanceId.Span.SequenceEqual(
                generatedTwo.ClientInstanceId.Span),
            "separate CSPRNG instance IDs differ");

        var server = EncodeServerPreface();
        for (var length = 0; length < server.Length; length++)
        {
            Check.True(
                !SecurePrefaceCodec.TryDecodeServer(
                    server.AsSpan(0, length),
                    SecureEndpointRole.Login,
                    out _),
                $"server preface truncation {length} rejects");
        }
        foreach (var offset in new[] { 0, 4, 6, 8, 10, 11, 12, 16, 20, 22 })
        {
            var mutated = (byte[])server.Clone();
            mutated[offset] ^= offset == 10 ? (byte)0x7F : (byte)0x01;
            Check.True(
                !SecurePrefaceCodec.TryDecodeServer(
                    mutated,
                    SecureEndpointRole.Login,
                    out _),
                $"server preface field mutation at {offset} rejects");
        }

        var zeroSuccessId = (byte[])server.Clone();
        zeroSuccessId.AsSpan(24, 16).Clear();
        Check.True(
            !SecurePrefaceCodec.TryDecodeServer(
                zeroSuccessId,
                SecureEndpointRole.Login,
                out _),
            "successful server preface requires nonzero ID");

        var nonzeroRejectedId = (byte[])server.Clone();
        nonzeroRejectedId[10] = (byte)SecureServerPrefaceStatus.ServerBusy;
        Check.True(
            !SecurePrefaceCodec.TryDecodeServer(
                nonzeroRejectedId,
                SecureEndpointRole.Login,
                out _),
            "rejected server preface requires zero ID");
        Check.Throws<ArgumentException>(
            () => new SecureServerPreface(
                SecureServerPrefaceStatus.Ok,
                SecureEndpointRole.Login,
                new byte[16]),
            "server model rejects zero success ID");
    }

    private static byte[] EncodeClientPreface()
    {
        var encoded = new byte[SecureProtocolConstants.ClientPrefaceBytes];
        Check.True(
            SecurePrefaceCodec.TryEncodeClient(
                new SecureClientPreface(
                    SecureEndpointRole.Login,
                    ClientInstanceId,
                    OriginHash),
                encoded,
                out _),
            "client preface fixture encodes");
        return encoded;
    }

    private static byte[] EncodeServerPreface()
    {
        var encoded = new byte[SecureProtocolConstants.ServerPrefaceBytes];
        Check.True(
            SecurePrefaceCodec.TryEncodeServer(
                new SecureServerPreface(
                    SecureServerPrefaceStatus.Ok,
                    SecureEndpointRole.Login,
                    ConnectionId),
                encoded,
                out _),
            "server preface fixture encodes");
        return encoded;
    }

    private static byte[] EncodeFrame(
        SecureFrameHeader header,
        ReadOnlySpan<byte> payload,
        SecureEndpointRole role,
        SecureFrameDirection direction)
    {
        var encoded = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            payload.Length];
        Check.True(
            SecureFrameCodec.TryEncode(
                header,
                payload,
                role,
                direction,
                encoded,
                out var bytesWritten),
            "frame fixture encodes");
        Check.Equal(encoded.Length, bytesWritten, "frame fixture length");
        return encoded;
    }
}
