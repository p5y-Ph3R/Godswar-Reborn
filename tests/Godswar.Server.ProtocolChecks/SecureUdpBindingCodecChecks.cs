using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpBindingCodecChecks
{
    private static readonly byte[] ConnectionId =
        Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();

    private static readonly byte[] Nonce =
        Enumerable.Range(0xA0, 16).Select(static value => (byte)value).ToArray();

    public static void Run()
    {
        CheckGoldenVector();
        CheckChallengeRoundTrip();
        CheckBoundariesAndMutations();
        CheckDecodeAllocationBound();
        CheckAdversarialInputs();
    }

    private static void CheckGoldenVector()
    {
        var encoded = new byte[SecureUdpBindingConstants.DatagramBytes];
        Check.True(
            SecureUdpAddressValidation.TryEncodeClientHello(
                ConnectionId,
                Nonce,
                encoded,
                out var written),
            "UDP ClientHello encodes");
        Check.Equal(128, written, "UDP ClientHello length");

        var expected = Convert.FromHexString(
            "475753550030010001000080" +
            "0102030405060708090A0B0C0D0E0F10" +
            "00000000" +
            "0000000000000000" +
            "0030" +
            "000000000000" +
            "A0A1A2A3A4A5A6A7A8A9AAABACADAEAF" +
            new string('0', 16) +
            new string('0', 48) +
            new string('0', 64));
        Check.True(
            encoded.SequenceEqual(expected),
            "UDP ClientHello golden bytes and network byte order");
        Check.True(
            SecureUdpBindingCodec.TryDecode(encoded, out var decoded),
            "UDP ClientHello decodes");
        Check.True(
            decoded.Type == SecureUdpBindingType.ClientHello,
            "UDP ClientHello type");
        Check.True(
            decoded.ConnectionId.SequenceEqual(ConnectionId),
            "UDP connection ID round trips");
        Check.True(
            decoded.ClientNonce.SequenceEqual(Nonce),
            "UDP client nonce round trips");
    }

    private static void CheckChallengeRoundTrip()
    {
        var tag = Enumerable.Range(0x40, 32)
            .Select(static value => (byte)value)
            .ToArray();
        var encoded = new byte[SecureUdpBindingConstants.DatagramBytes];
        Check.True(
            SecureUdpBindingCodec.TryEncode(
                SecureUdpBindingType.ServerChallenge,
                ConnectionId,
                0x01020304,
                0,
                Nonce,
                0x0102030405060708,
                tag,
                encoded,
                out var written),
            "UDP challenge encodes");
        Check.Equal(128, written, "UDP challenge length");
        Check.True(
            encoded.AsSpan(28, 4).SequenceEqual(
                Convert.FromHexString("01020304")),
            "UDP key epoch uses network byte order");
        Check.True(
            encoded.AsSpan(64, 8).SequenceEqual(
                Convert.FromHexString("0102030405060708")),
            "UDP issue time uses network byte order");
        Check.True(
            SecureUdpBindingCodec.TryDecode(encoded, out var decoded),
            "UDP challenge decodes");
        Check.True(
            decoded.Type == SecureUdpBindingType.ServerChallenge,
            "UDP challenge type");
        Check.Equal(0x01020304u, decoded.KeyEpoch, "UDP key epoch");
        Check.Equal(
            0x0102030405060708L,
            decoded.IssuedAtUnixSeconds,
            "UDP issue time");
        Check.True(
            decoded.Authenticator.SequenceEqual(tag),
            "UDP full HMAC tag round trips");
    }

    private static void CheckBoundariesAndMutations()
    {
        var hello = EncodeHello();
        for (var length = 0; length < hello.Length; length++)
        {
            Check.True(
                !SecureUdpBindingCodec.TryDecode(
                    hello.AsSpan(0, length),
                    out _),
                $"UDP truncation {length} rejects");
        }

        for (var length = 129; length <= 1_201; length++)
        {
            var oversized = new byte[length];
            hello.CopyTo(oversized, 0);
            Check.True(
                !SecureUdpBindingCodec.TryDecode(oversized, out _),
                $"UDP non-binding length {length} rejects");
        }

        foreach (var offset in new[]
        {
            0, 4, 6, 7, 8, 9, 10, 28, 32, 40, 42, 64, 72, 96
        })
        {
            var mutated = (byte[])hello.Clone();
            mutated[offset] ^= 0x01;
            Check.True(
                !SecureUdpBindingCodec.TryDecode(mutated, out _),
                $"UDP field mutation at {offset} rejects");
        }

        var zeroConnection = (byte[])hello.Clone();
        Array.Clear(zeroConnection, 12, 16);
        Check.True(
            !SecureUdpBindingCodec.TryDecode(zeroConnection, out _),
            "zero UDP connection ID rejects");
        var zeroNonce = (byte[])hello.Clone();
        Array.Clear(zeroNonce, 48, 16);
        Check.True(
            !SecureUdpBindingCodec.TryDecode(zeroNonce, out _),
            "zero UDP nonce rejects");

        Check.True(
            !SecureUdpAddressValidation.TryEncodeClientHello(
                ConnectionId,
                Nonce,
                new byte[127],
                out var written),
            "short UDP encode destination rejects");
        Check.Equal(0, written, "failed UDP encode writes zero bytes");
    }

    private static void CheckDecodeAllocationBound()
    {
        var hello = EncodeHello();
        for (var index = 0; index < 100; index++)
        {
            _ = SecureUdpBindingCodec.TryDecode(hello, out _);
        }

        var allocated = long.MaxValue;
        var decoded = false;
        for (var attempt = 0;
             attempt < 3 && allocated != 0;
             attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            decoded = true;
            for (var index = 0; index < 10_000; index++)
            {
                decoded &=
                    SecureUdpBindingCodec.TryDecode(hello, out _);
            }
            allocated =
                GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Check.True(decoded, "warmed UDP decode succeeds");
        Check.Equal(0L, allocated, "UDP decoder allocation bound");
    }

    private static void CheckAdversarialInputs()
    {
        var random = new Random(0x5A17);
        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var bytes = new byte[random.Next(0, 1_202)];
            random.NextBytes(bytes);
            _ = SecureUdpBindingCodec.TryDecode(bytes, out _);
        }
    }

    internal static byte[] EncodeHello()
    {
        var hello = new byte[SecureUdpBindingConstants.DatagramBytes];
        Check.True(
            SecureUdpAddressValidation.TryEncodeClientHello(
                ConnectionId,
                Nonce,
                hello,
                out var written) &&
            written == hello.Length,
            "UDP hello fixture");
        return hello;
    }

    internal static ReadOnlySpan<byte> TestConnectionId => ConnectionId;

    internal static ReadOnlySpan<byte> TestNonce => Nonce;
}
