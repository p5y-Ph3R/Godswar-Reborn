using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpProtectedCodecChecks
{
    private const string GoldenKey =
        "C27A8E9BF928AE027A3915F49E942F9273CE975F27CD775CC2E7ED894A00D5FA";

    private const string GoldenHeader =
        "475753500040010001000060" +
        "101112131415161718191A1B1C1D1E1F" +
        "00000001" +
        "0000000000000000" +
        "00000000" +
        "0000000000000000" +
        "0000000000000000" +
        "01" +
        "00" +
        "0010";

    private const string GoldenPlaintext =
        "000000000000000100000000075BCD15";

    private const string GoldenCiphertext =
        "36486AB35FD8E6650AB613A49B881EDD";

    private const string GoldenTag =
        "7D174FF3A7946AA12C991108036242C6";

    public static void Run()
    {
        CheckGoldenVector();
        CheckDirectionalAndEpochKeySeparation();
        CheckHeaderAndMessageBounds();
        CheckAuthenticationAndAdversarialInputs();
        CheckHeaderDecoderAllocationBound();
    }

    private static void CheckGoldenVector()
    {
        var key = SecureUdpProtectedTestData.DeriveKey(
            SecureUdpTrafficDirection.ClientToServer);
        Check.True(
            key.SequenceEqual(Convert.FromHexString(GoldenKey)),
            "protected UDP HKDF-SHA256 golden key");

        var plaintext = SecureUdpProtectedTestData.CreatePing();
        Check.True(
            plaintext.SequenceEqual(
                Convert.FromHexString(GoldenPlaintext)),
            "protected UDP golden Ping payload");
        var header = SecureUdpProtectedTestData.CreateHeader(
            SecureUdpProtectedMessageType.Ping,
            plaintext.Length);
        var datagram = new byte[
            SecureUdpProtectedConstants.MaximumDatagramBytes];
        Check.True(
            SecureUdpProtectedCodec.TryEncrypt(
                header,
                key,
                plaintext,
                datagram,
                out var written,
                out var error),
            $"protected UDP golden encrypt ({error})");
        Check.Equal(96, written, "protected UDP golden datagram size");

        var expected = Convert.FromHexString(
            GoldenHeader + GoldenCiphertext + GoldenTag);
        Check.True(
            datagram.AsSpan(0, written).SequenceEqual(expected),
            "protected UDP HKDF and AES-GCM golden datagram");
        Check.True(
            datagram.AsSpan(0, 64).SequenceEqual(
                Convert.FromHexString(GoldenHeader)),
            "protected UDP authenticated header golden bytes");

        var decrypted = new byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        Check.True(
            SecureUdpProtectedCodec.TryDecrypt(
                expected,
                key,
                decrypted,
                out var decoded,
                out var payloadBytes,
                out error),
            $"protected UDP golden decrypt ({error})");
        Check.Equal(plaintext.Length, payloadBytes, "golden payload size");
        Check.True(
            decrypted.AsSpan(0, payloadBytes).SequenceEqual(plaintext),
            "protected UDP golden plaintext");
        Check.Equal(1u, decoded.KeyEpoch, "golden key epoch");
        Check.Equal(0UL, decoded.Sequence, "golden sequence");
        Check.True(
            decoded.MessageType ==
                SecureUdpProtectedMessageType.Ping,
            "golden message type");
        Check.True(
            decoded.Acknowledgement ==
                SecureUdpAcknowledgement.None,
            "golden acknowledgement");

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(decrypted);
    }

    private static void CheckDirectionalAndEpochKeySeparation()
    {
        var clientEpochOne = SecureUdpProtectedTestData.DeriveKey(
            SecureUdpTrafficDirection.ClientToServer,
            1);
        var serverEpochOne = SecureUdpProtectedTestData.DeriveKey(
            SecureUdpTrafficDirection.ServerToClient,
            1);
        var clientEpochTwo = SecureUdpProtectedTestData.DeriveKey(
            SecureUdpTrafficDirection.ClientToServer,
            2);
        Check.True(
            !clientEpochOne.SequenceEqual(serverEpochOne),
            "directional traffic keys differ");
        Check.True(
            !clientEpochOne.SequenceEqual(clientEpochTwo),
            "key epochs derive different traffic keys");

        var badSecret = new byte[
            SecureUdpProtectedConstants.KeyBytes];
        var output = new byte[
            SecureUdpProtectedConstants.KeyBytes];
        Check.True(
            !SecureUdpTrafficKeyDerivation.TryDeriveKey(
                badSecret,
                SecureUdpProtectedTestData.ConnectionId,
                SecureUdpProtectedTestData.ServerId,
                SecureUdpTrafficDirection.ClientToServer,
                1,
                output),
            "all-zero binding secret rejects");
        Check.True(
            !SecureUdpTrafficKeyDerivation.TryDeriveKey(
                SecureUdpProtectedTestData.BindingSecret,
                SecureUdpProtectedTestData.ConnectionId,
                SecureUdpProtectedTestData.ServerId,
                SecureUdpTrafficDirection.ClientToServer,
                0,
                output),
            "zero traffic-key epoch rejects");

        CryptographicOperations.ZeroMemory(clientEpochOne);
        CryptographicOperations.ZeroMemory(serverEpochOne);
        CryptographicOperations.ZeroMemory(clientEpochTwo);
    }

    private static void CheckHeaderAndMessageBounds()
    {
        var golden = Convert.FromHexString(
            GoldenHeader + GoldenCiphertext + GoldenTag);
        Check.True(
            SecureUdpProtectedCodec.TryDecodeHeader(
                golden,
                out var header),
            "protected UDP golden header decodes");
        Check.Equal(96, header.DatagramBytes, "decoded total bytes");

        for (var length = 0; length < golden.Length; length++)
        {
            Check.True(
                !SecureUdpProtectedCodec.TryDecodeHeader(
                    golden.AsSpan(0, length),
                    out _),
                $"protected UDP truncation {length} rejects");
        }
        var trailing = new byte[golden.Length + 1];
        golden.CopyTo(trailing, 0);
        Check.True(
            !SecureUdpProtectedCodec.TryDecodeHeader(trailing, out _),
            "protected UDP trailing byte rejects");
        Check.True(
            !SecureUdpProtectedCodec.TryDecodeHeader(
                new byte[
                    SecureUdpProtectedConstants.MaximumDatagramBytes + 1],
                out _),
            "protected UDP path-MTU overflow rejects");

        foreach (var offset in new[]
        {
            0, 4, 6, 7, 8, 9, 10, 12, 28, 40, 44, 52, 60, 61, 62
        })
        {
            var mutated = (byte[])golden.Clone();
            mutated[offset] ^= 0x80;
            if (offset is 12 or 28 or 40 or 44 or 52)
            {
                continue;
            }
            Check.True(
                !SecureUdpProtectedCodec.TryDecodeHeader(
                    mutated,
                    out _),
                $"protected UDP structural mutation {offset} rejects");
        }

        var invalidAck = (byte[])golden.Clone();
        invalidAck[51] = 1;
        Check.True(
            !SecureUdpProtectedCodec.TryDecodeHeader(
                invalidAck,
                out _),
            "ack sequence without ack epoch rejects");
        var underflowAck = new SecureUdpAcknowledgement(1, 1, 0x2);
        Check.True(
            !underflowAck.IsValid(),
            "ack mask cannot describe wrapped sequence");
        Check.True(
            new SecureUdpAcknowledgement(1, 64, ulong.MaxValue)
                .IsValid(),
            "full 64-bit acknowledgement mask is bounded");

        var shortDestination = new byte[95];
        var key = SecureUdpProtectedTestData.DeriveKey(
            SecureUdpTrafficDirection.ClientToServer);
        var ping = SecureUdpProtectedTestData.CreatePing();
        Check.True(
            !SecureUdpProtectedCodec.TryEncrypt(
                SecureUdpProtectedTestData.CreateHeader(
                    SecureUdpProtectedMessageType.Ping,
                    ping.Length),
                key,
                ping,
                shortDestination,
                out var written,
                out _),
            "short protected UDP destination rejects");
        Check.Equal(0, written, "failed protected encode writes zero");
        CryptographicOperations.ZeroMemory(key);
    }

    private static void CheckAuthenticationAndAdversarialInputs()
    {
        var golden = Convert.FromHexString(
            GoldenHeader + GoldenCiphertext + GoldenTag);
        var key = SecureUdpProtectedTestData.DeriveKey(
            SecureUdpTrafficDirection.ClientToServer);
        var plaintext = new byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        for (var offset = 0; offset < golden.Length; offset++)
        {
            var mutated = (byte[])golden.Clone();
            mutated[offset] ^= 1;
            Check.True(
                !SecureUdpProtectedCodec.TryDecrypt(
                    mutated,
                    key,
                    plaintext,
                    out _,
                    out _,
                    out _),
                $"protected UDP mutation {offset} rejects");
        }

        var wrongKey = (byte[])key.Clone();
        wrongKey[0] ^= 1;
        Check.True(
            !SecureUdpProtectedCodec.TryDecrypt(
                golden,
                wrongKey,
                plaintext,
                out _,
                out _,
                out var error) &&
            error == SecureUdpProtectedError.AuthenticationFailed,
            "wrong protected UDP traffic key rejects");

        var random = new Random(0x47575350);
        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var bytes = new byte[random.Next(
                0,
                SecureUdpProtectedConstants.MaximumDatagramBytes + 2)];
            random.NextBytes(bytes);
            _ = SecureUdpProtectedCodec.TryDecodeHeader(bytes, out _);
            _ = SecureUdpProtectedCodec.TryDecrypt(
                bytes,
                key,
                plaintext,
                out _,
                out _,
                out _);
        }

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(wrongKey);
        CryptographicOperations.ZeroMemory(plaintext);
    }

    private static void CheckHeaderDecoderAllocationBound()
    {
        var golden = Convert.FromHexString(
            GoldenHeader + GoldenCiphertext + GoldenTag);
        for (var index = 0; index < 100; index++)
        {
            _ = SecureUdpProtectedCodec.TryDecodeHeader(golden, out _);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            Check.True(
                SecureUdpProtectedCodec.TryDecodeHeader(golden, out _),
                "warmed protected UDP header decode");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Equal(
            0L,
            allocated,
            "protected UDP structural decoder allocation bound");
    }
}
