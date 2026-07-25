using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpBindingGrantChecks
{
    private static readonly byte[] ConnectionId =
        Enumerable.Range(1, 16)
            .Select(static value => checked((byte)value))
            .ToArray();

    private static readonly byte[] ProofKey =
        Enumerable.Range(0xA0, 32)
            .Select(static value => checked((byte)value))
            .ToArray();

    public static void Run()
    {
        CheckRoundTripAndBoundaries();
        CheckFramePolicy();
        CheckSecretLifecycle();
    }

    private static void CheckRoundTripAndBoundaries()
    {
        using var grant = CreateGrant();
        var encoded = new byte[
            SecureProtocolConstants.UdpBindingGrantBytes];
        Check.True(
            SecureUdpBindingGrantCodec.TryEncode(
                grant,
                encoded,
                out var written),
            "UDP TLS grant encodes");
        Check.Equal(encoded.Length, written, "UDP TLS grant bytes");
        Check.Equal(
            "47575547000100001D140000000000640000018BCFE568000102030405060708090A0B0C0D0E0F10A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF",
            Convert.ToHexString(encoded),
            "UDP TLS grant golden vector");

        Check.True(
            SecureUdpBindingGrantCodec.TryDecode(
                encoded,
                out var decoded),
            "UDP TLS grant decodes");
        using (decoded)
        {
            Check.True(
                decoded!.UdpPort == 7_444 &&
                decoded.ServerId == 100 &&
                decoded.ExpiryUnixMilliseconds ==
                    1_700_000_000_000,
                "UDP TLS grant public scope");
            var connectionId = new byte[16];
            var proofKey = new byte[32];
            Check.True(
                decoded.TryCopySecrets(connectionId, proofKey) &&
                connectionId.SequenceEqual(ConnectionId) &&
                proofKey.SequenceEqual(ProofKey),
                "UDP TLS grant secret material round trips");
        }

        for (var length = 0; length < encoded.Length; length++)
        {
            Check.True(
                !SecureUdpBindingGrantCodec.TryDecode(
                    encoded.AsSpan(0, length),
                    out _),
                $"truncated UDP TLS grant {length} rejects");
        }
        var oversized = new byte[encoded.Length + 1];
        encoded.CopyTo(oversized, 0);
        Check.True(
            !SecureUdpBindingGrantCodec.TryDecode(
                oversized,
                out _),
            "oversized UDP TLS grant rejects");
        foreach (var offset in new[] { 0, 4, 6, 10 })
        {
            var malformed = (byte[])encoded.Clone();
            malformed[offset] ^= 0x01;
            Check.True(
                !SecureUdpBindingGrantCodec.TryDecode(
                    malformed,
                    out _),
                $"mutated UDP TLS grant field {offset} rejects");
        }
        foreach (var range in new[]
        {
            (Offset: 8, Length: 2),
            (Offset: 12, Length: 4),
            (Offset: 16, Length: 8),
            (Offset: 24, Length: 16),
            (Offset: 40, Length: 32)
        })
        {
            var malformed = (byte[])encoded.Clone();
            malformed.AsSpan(range.Offset, range.Length).Clear();
            Check.True(
                !SecureUdpBindingGrantCodec.TryDecode(
                    malformed,
                    out _),
                $"zero UDP TLS grant field {range.Offset} rejects");
        }
    }

    private static void CheckFramePolicy()
    {
        using var grant = CreateGrant();
        var payload = new byte[
            SecureProtocolConstants.UdpBindingGrantBytes];
        Check.True(
            SecureUdpBindingGrantCodec.TryEncode(
                grant,
                payload,
                out _),
            "UDP grant frame fixture");
        var frame = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            payload.Length];
        var header = new SecureFrameHeader(
            checked((uint)payload.Length),
            SecureFrameType.UdpBindingGrant,
            Sequence: 2);
        Check.True(
            SecureFrameCodec.TryEncode(
                header,
                payload,
                SecureEndpointRole.Game,
                SecureFrameDirection.ServerToClient,
                frame,
                out var written) &&
            written == frame.Length,
            "UDP grant is game server-to-client only");
        Check.True(
            !SecureFrameCodec.TryEncode(
                header,
                payload,
                SecureEndpointRole.Game,
                SecureFrameDirection.ClientToServer,
                frame,
                out _),
            "client cannot send UDP grant frame");
        Check.True(
            !SecureFrameCodec.TryEncode(
                header,
                payload,
                SecureEndpointRole.Login,
                SecureFrameDirection.ServerToClient,
                frame,
                out _),
            "login endpoint cannot send UDP grant frame");
    }

    private static void CheckSecretLifecycle()
    {
        var grant = CreateGrant();
        grant.Dispose();
        var connectionId = Enumerable.Repeat((byte)0xCC, 16).ToArray();
        var proofKey = Enumerable.Repeat((byte)0xCC, 32).ToArray();
        Check.True(
            !grant.TryCopySecrets(connectionId, proofKey) &&
            connectionId.All(static value => value == 0) &&
            proofKey.All(static value => value == 0),
            "disposed UDP grant rejects and clears secret output");
        var destination = Enumerable.Repeat(
                (byte)0xCC,
                SecureProtocolConstants.UdpBindingGrantBytes)
            .ToArray();
        Check.True(
            !SecureUdpBindingGrantCodec.TryEncode(
                grant,
                destination,
                out var written) &&
            written == 0 &&
            destination.All(static value => value == 0),
            "failed UDP grant encode clears destination");
    }

    private static SecureUdpBindingGrant CreateGrant() =>
        new(
            udpPort: 7_444,
            serverId: 100,
            expiryUnixMilliseconds: 1_700_000_000_000,
            ConnectionId,
            ProofKey);
}
