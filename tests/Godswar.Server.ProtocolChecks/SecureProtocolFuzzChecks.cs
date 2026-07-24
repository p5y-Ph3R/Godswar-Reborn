using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureProtocolCodecChecks
{
    private const int AdversarialIterations = 5_000;

    private static void CheckBoundedAdversarialInputs()
    {
        var random = new Random(0x5EC0_2026);
        var storage = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            SecureProtocolConstants.MaximumPayloadBytes +
            1];
        var secretGrantId = new byte[SecureProtocolConstants.GrantIdBytes];
        var secretTicket = new byte[SecureProtocolConstants.TicketBytes];

        for (var iteration = 0; iteration < AdversarialIterations; iteration++)
        {
            var length = random.Next(storage.Length + 1);
            var input = storage.AsSpan(0, length);
            random.NextBytes(input);

            SecurePrefaceCodec.TryDecodeClient(
                input,
                SecureEndpointRole.Login,
                out _);
            SecurePrefaceCodec.TryDecodeClient(
                input,
                SecureEndpointRole.Game,
                out _);
            SecurePrefaceCodec.TryDecodeServer(
                input,
                SecureEndpointRole.Login,
                out _);
            SecurePrefaceCodec.TryDecodeServer(
                input,
                SecureEndpointRole.Game,
                out _);
            SecurePrefaceCodec.ReadClient(
                input,
                SecureEndpointRole.Login,
                out _,
                out _);
            SecurePrefaceCodec.ReadServer(
                input,
                SecureEndpointRole.Game,
                out _,
                out _);
            if (SecureFrameCodec.Read(
                    input,
                    SecureEndpointRole.Game,
                    SecureFrameDirection.ClientToServer,
                    1,
                    out var readHeader,
                    out var bytesConsumed) == SecureDecodeStatus.Done)
            {
                Check.True(
                    bytesConsumed is >=
                        SecureProtocolConstants.FrameHeaderBytes and <=
                        SecureProtocolConstants.FrameHeaderBytes +
                        SecureProtocolConstants.MaximumPayloadBytes,
                    "random incremental frame consumption is bounded");
                Check.True(
                    readHeader.PayloadLength ==
                        bytesConsumed -
                        SecureProtocolConstants.FrameHeaderBytes,
                    "random incremental frame consumption is exact");
            }

            if (SecureFrameCodec.TryDecode(
                    input,
                    SecureEndpointRole.Login,
                    SecureFrameDirection.ClientToServer,
                    1,
                    out var loginInbound))
            {
                CheckDecodedFrameInvariant(
                    input,
                    loginInbound,
                    SecureEndpointRole.Login,
                    SecureFrameDirection.ClientToServer);
            }
            if (SecureFrameCodec.TryDecode(
                    input,
                    SecureEndpointRole.Game,
                    SecureFrameDirection.ServerToClient,
                    1,
                    out var gameOutbound))
            {
                CheckDecodedFrameInvariant(
                    input,
                    gameOutbound,
                    SecureEndpointRole.Game,
                    SecureFrameDirection.ServerToClient);
            }

            if (SecureGameControlCodec.TryDecodeGrant(input, out var grant))
            {
                using (grant)
                {
                    Check.True(grant is not null, "random decoded grant exists");
                    Check.True(
                        grant!.TargetServerId != 0,
                        "random decoded grant target is nonzero");
                    Check.True(
                        grant.TryCopySecrets(secretGrantId, secretTicket),
                        "random decoded grant secrets can be borrowed");
                    Check.True(
                        secretGrantId.AsSpan().IndexOfAnyExcept((byte)0) >= 0,
                        "random decoded grant ID is nonzero");
                    Check.True(
                        secretTicket.AsSpan().IndexOfAnyExcept((byte)0) >= 0,
                        "random decoded grant ticket is nonzero");
                    secretGrantId.AsSpan().Clear();
                    secretTicket.AsSpan().Clear();
                }
            }
            if (SecureGameControlCodec.TryDecodeBind(input, out var bind))
            {
                using (bind)
                {
                    Check.True(bind is not null, "random decoded bind exists");
                    Check.True(
                        bind!.TryCopySecrets(secretGrantId, secretTicket),
                        "random decoded bind secrets can be borrowed");
                    Check.True(
                        secretGrantId.AsSpan().IndexOfAnyExcept((byte)0) >= 0,
                        "random decoded bind grant ID is nonzero");
                    Check.True(
                        secretTicket.AsSpan().IndexOfAnyExcept((byte)0) >= 0,
                        "random decoded bind ticket is nonzero");
                    secretGrantId.AsSpan().Clear();
                    secretTicket.AsSpan().Clear();
                }
            }
            if (SecureGameControlCodec.TryDecodeBindResult(
                    input,
                    out var result))
            {
                Check.True(
                    Enum.IsDefined(result.Status),
                    "random decoded bind result has finite status");
            }
        }
    }

    private static void CheckDecodedFrameInvariant(
        ReadOnlySpan<byte> source,
        SecureFrameHeader header,
        SecureEndpointRole role,
        SecureFrameDirection direction)
    {
        Check.Equal(1UL, header.Sequence, "random decoded sequence");
        Check.True(
            header.PayloadLength <=
                SecureProtocolConstants.MaximumPayloadBytes,
            "random decoded payload remains bounded");

        var payload = source.Slice(
            SecureProtocolConstants.FrameHeaderBytes,
            (int)header.PayloadLength);
        var destination = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            payload.Length];
        Check.True(
            SecureFrameCodec.TryEncode(
                header,
                payload,
                role,
                direction,
                destination,
                out var bytesWritten),
            "random successful frame can re-encode");
        Check.Equal(
            destination.Length,
            bytesWritten,
            "random re-encoded frame length");
    }
}
