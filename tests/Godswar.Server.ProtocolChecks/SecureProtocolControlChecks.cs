using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureProtocolCodecChecks
{
    private static void CheckGameControlGoldenVectors()
    {
        using var grant = NewGrant();
        var encodedGrant = new byte[SecureProtocolConstants.MaximumGameGrantBytes];
        Check.True(
            SecureGameControlCodec.TryEncodeGrant(
                grant,
                encodedGrant,
                out var grantBytesWritten),
            "game grant encodes");
        Check.Equal(71, grantBytesWritten, "minimum game grant length");
        var expectedGrant = Convert.FromHexString(
            "01010101176F1D130000002A0102030405060708" +
            "0102030405060708090A0B0C0D0E0F10" +
            "202122232425262728292A2B2C2D2E2F" +
            "303132333435363738393A3B3C3D3E3F" +
            "616263");
        Check.True(
            encodedGrant.AsSpan(0, grantBytesWritten).SequenceEqual(
                expectedGrant),
            "game grant golden bytes and network byte order");
        Check.True(
            SecureGameControlCodec.TryDecodeGrant(
                expectedGrant,
                out var decodedGrant),
            "game grant decodes");
        using (decodedGrant)
        {
            Check.True(decodedGrant is not null, "decoded grant exists");
            Check.Equal("a", decodedGrant!.RouteHost, "route host");
            Check.Equal("b", decodedGrant.TlsHost, "TLS host");
            Check.Equal("c", decodedGrant.Audience, "audience");
            Check.Equal((ushort)5999, decodedGrant.RoutePort, "route port");
            Check.Equal((ushort)7443, decodedGrant.TlsPort, "TLS port");
            Check.Equal(42U, decodedGrant.TargetServerId, "target server ID");
            Check.Equal(
                0x0102030405060708UL,
                decodedGrant.ExpiryUnixMilliseconds,
                "grant expiry");
            Span<byte> decodedGrantId = stackalloc byte[16];
            Span<byte> decodedTicket = stackalloc byte[32];
            Check.True(
                decodedGrant.TryCopySecrets(
                    decodedGrantId,
                    decodedTicket),
                "decoded grant secrets can be borrowed");
            Check.True(
                decodedGrantId.SequenceEqual(GrantId),
                "grant ID round trips");
            Check.True(
                decodedTicket.SequenceEqual(Ticket),
                "ticket round trips");
            decodedGrantId.Clear();
            decodedTicket.Clear();
        }

        using var bind = new SecureGameBind(GrantId, Ticket);
        var encodedBind = new byte[SecureProtocolConstants.GameBindBytes];
        Check.True(
            SecureGameControlCodec.TryEncodeBind(
                bind,
                encodedBind,
                out var bindBytesWritten),
            "game bind encodes");
        Check.Equal(52, bindBytesWritten, "game bind length");
        var expectedBind = Convert.FromHexString(
            "01000000" +
            "0102030405060708090A0B0C0D0E0F10" +
            "202122232425262728292A2B2C2D2E2F" +
            "303132333435363738393A3B3C3D3E3F");
        Check.True(
            encodedBind.SequenceEqual(expectedBind),
            "game bind golden bytes");
        Check.True(
            SecureGameControlCodec.TryDecodeBind(
                expectedBind,
                out var decodedBind),
            "game bind decodes");
        using (decodedBind)
        {
            Check.True(decodedBind is not null, "decoded bind exists");
            Span<byte> decodedGrantId = stackalloc byte[16];
            Span<byte> decodedTicket = stackalloc byte[32];
            Check.True(
                decodedBind!.TryCopySecrets(
                    decodedGrantId,
                    decodedTicket),
                "decoded bind secrets can be borrowed");
            Check.True(
                decodedGrantId.SequenceEqual(GrantId),
                "bind grant ID");
            Check.True(
                decodedTicket.SequenceEqual(Ticket),
                "bind ticket");
            decodedGrantId.Clear();
            decodedTicket.Clear();
        }

        foreach (var status in Enum.GetValues<SecureBindStatus>())
        {
            var encodedResult = new byte[SecureProtocolConstants.BindResultBytes];
            Check.True(
                SecureGameControlCodec.TryEncodeBindResult(
                    new SecureBindResult(status),
                    encodedResult,
                    out var resultBytesWritten),
                $"bind result {status} encodes");
            Check.Equal(4, resultBytesWritten, "bind result length");
            Check.True(
                SecureGameControlCodec.TryDecodeBindResult(
                    encodedResult,
                    out var decodedResult),
                $"bind result {status} decodes");
            Check.True(
                decodedResult.Status == status,
                "bind status");
            Check.Equal(
                status == SecureBindStatus.Accepted,
                decodedResult.IsAccepted,
                "only accepted status enables game traffic");
        }
    }

    private static void CheckGameControlBoundaries()
    {
        using var maximumGrant = new SecureGameGrant(
            new string('r', 23),
            string.Join(
                '.',
                new string('a', 63),
                new string('b', 63),
                new string('c', 63),
                new string('d', 61)),
            new string('A', 64),
            1,
            ushort.MaxValue,
            uint.MaxValue,
            ulong.MaxValue,
            GrantId,
            Ticket);
        var maximumBytes = new byte[SecureProtocolConstants.MaximumGameGrantBytes];
        Check.True(
            SecureGameControlCodec.TryEncodeGrant(
                maximumGrant,
                maximumBytes,
                out var maximumWritten),
            "maximum game grant encodes");
        Check.Equal(408, maximumWritten, "maximum game grant length");
        Check.True(
            SecureGameControlCodec.TryDecodeGrant(
                maximumBytes,
                out var decodedMaximum),
            "maximum game grant decodes");
        decodedMaximum?.Dispose();

        for (var length = 0; length < 71; length++)
        {
            Check.True(
                !SecureGameControlCodec.TryDecodeGrant(
                    maximumBytes.AsSpan(0, length),
                    out _),
                $"grant truncation {length} rejects");
        }
        Check.True(
            !SecureGameControlCodec.TryDecodeGrant(
                maximumBytes.Concat(new byte[] { 0 }).ToArray(),
                out _),
            "grant trailing byte rejects");

        var minimum = EncodeMinimumGrant();
        foreach (var offset in new[] { 0, 1, 2, 3, 4, 6, 8, 20, 36, 68, 69 })
        {
            var mutated = (byte[])minimum.Clone();
            switch (offset)
            {
                case 1:
                case 2:
                case 3:
                    mutated[offset] = 0;
                    break;
                case 4:
                case 6:
                    mutated[offset] = 0;
                    mutated[offset + 1] = 0;
                    break;
                case 8:
                    mutated.AsSpan(8, 4).Clear();
                    break;
                case 20:
                    mutated.AsSpan(20, 16).Clear();
                    break;
                case 36:
                    mutated.AsSpan(36, 32).Clear();
                    break;
                default:
                    mutated[offset] ^= 0x80;
                    break;
            }

            Check.True(
                !SecureGameControlCodec.TryDecodeGrant(mutated, out _),
                $"invalid grant field at {offset} rejects");
        }

        foreach (var invalidHost in new[]
                 {
                     "UPPER", "-edge", "edge-", "double..dot",
                     "under_score", "trailing.", "space name"
                 })
        {
            Check.Throws<ArgumentException>(
                () => new SecureGameGrant(
                    invalidHost,
                    "game.reborn.test",
                    "world",
                    1,
                    2,
                    3,
                    4,
                    GrantId,
                    Ticket),
                $"invalid route host {invalidHost} rejects");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => new SecureGameGrant(
                "route",
                "game.reborn.test",
                "world",
                1,
                2,
                0,
                4,
                GrantId,
                Ticket),
            "zero target server ID rejects");
        Check.Throws<ArgumentException>(
            () => new SecureGameGrant(
                "route",
                "game.reborn.test",
                "world",
                1,
                2,
                3,
                4,
                new byte[16],
                Ticket),
            "zero grant ID rejects");
        Check.Throws<ArgumentException>(
            () => new SecureGameBind(GrantId, new byte[32]),
            "zero bind ticket rejects");

        var bind = new byte[SecureProtocolConstants.GameBindBytes];
        bind[0] = 1;
        GrantId.CopyTo(bind, 4);
        Ticket.CopyTo(bind, 20);
        for (var length = 0; length < bind.Length; length++)
        {
            Check.True(
                !SecureGameControlCodec.TryDecodeBind(
                    bind.AsSpan(0, length),
                    out _),
                $"bind truncation {length} rejects");
        }
        foreach (var reservedOffset in new[] { 1, 2, 3 })
        {
            var mutated = (byte[])bind.Clone();
            mutated[reservedOffset] = 1;
            Check.True(
                !SecureGameControlCodec.TryDecodeBind(mutated, out _),
                $"bind reserved byte {reservedOffset} rejects");
        }

        var unknownResult = new byte[] { 0, 4, 0, 0 };
        Check.True(
            !SecureGameControlCodec.TryDecodeBindResult(
                unknownResult,
                out _),
            "unknown bind status rejects");
        Check.True(
            !SecureGameControlCodec.TryEncodeBindResult(
                new SecureBindResult((SecureBindStatus)4),
                new byte[4],
                out _),
            "unknown bind status cannot encode");
        Check.True(
            !SecureGameControlCodec.TryDecodeBindResult(
                new byte[] { 0, 0, 0, 1 },
                out _),
            "bind result reserved byte rejects");
    }

    private static SecureGameGrant NewGrant()
    {
        return new SecureGameGrant(
            "a",
            "b",
            "c",
            5999,
            7443,
            42,
            0x0102030405060708UL,
            GrantId,
            Ticket);
    }

    private static byte[] EncodeMinimumGrant()
    {
        using var grant = NewGrant();
        var encoded = new byte[71];
        Check.True(
            SecureGameControlCodec.TryEncodeGrant(
                grant,
                encoded,
                out var bytesWritten),
            "minimum grant fixture encodes");
        Check.Equal(71, bytesWritten, "minimum grant fixture length");
        return encoded;
    }
}
