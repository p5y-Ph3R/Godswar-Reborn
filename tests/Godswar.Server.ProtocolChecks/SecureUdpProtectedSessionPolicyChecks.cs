using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureUdpProtectedSessionChecks
{
    private static void CheckDirectionAndPayloadPolicy()
    {
        var time = new ManualTimeProvider();
        using var client = CreateSession(SecureUdpPeerRole.Client, time);
        using var server = CreateSession(SecureUdpPeerRole.Server, time);
        var destination = new byte[
            SecureUdpProtectedConstants.MaximumDatagramBytes];

        Check.True(
            !client.TryProtect(
                SecureUdpProtectedMessageType.Pong,
                SecureUdpProtectedTestData.CreatePong(),
                destination,
                out _,
                out var error) &&
            error == SecureUdpProtectedError.InvalidMessageDirection,
            "client cannot send server-only Pong");
        Check.True(
            !server.TryProtect(
                SecureUdpProtectedMessageType.Ping,
                SecureUdpProtectedTestData.CreatePing(),
                destination,
                out _,
                out error) &&
            error == SecureUdpProtectedError.InvalidMessageDirection,
            "server cannot send client-only Ping");

        var invalidPing = new byte[
            SecureUdpProtectedConstants.PingPayloadBytes];
        Check.True(
            !client.TryProtect(
                SecureUdpProtectedMessageType.Ping,
                invalidPing,
                destination,
                out _,
                out error) &&
            error == SecureUdpProtectedError.InvalidPayload,
            "zero Ping identifier rejects before sequence use");
        Check.Equal(
            0UL,
            client.GetSnapshot().NextSendSequence,
            "invalid payload does not consume sequence");

        var wrongDirection = EncryptRaw(
            SecureUdpProtectedMessageType.Pong,
            SecureUdpProtectedTestData.CreatePong(),
            SecureUdpTrafficDirection.ClientToServer,
            keyEpoch: 2);
        var before = server.GetSnapshot();
        Check.True(
            !server.TryUnprotect(
                wrongDirection,
                destination,
                out _,
                out _,
                out error) &&
            error ==
                SecureUdpProtectedError.InvalidMessageDirection,
            "valid-AEAD wrong-direction message rejects");
        var after = server.GetSnapshot();
        Check.True(
            before.ReceiveKeyEpoch == after.ReceiveKeyEpoch &&
            before.ReceiveReplayBitsLow == after.ReceiveReplayBitsLow,
            "wrong-direction message cannot promote or commit replay");

        var authenticatedInvalid = EncryptRawContentUnchecked(
            new byte[
                SecureUdpProtectedConstants.PingPayloadBytes]);
        Check.True(
            !server.TryUnprotect(
                authenticatedInvalid,
                destination,
                out _,
                out _,
                out error) &&
            error == SecureUdpProtectedError.InvalidPayload,
            "authenticated malformed Ping payload rejects");
        Check.True(
            !server.GetSnapshot().HasReceivedCurrentEpoch,
            "invalid authenticated payload cannot commit replay");
    }

    private static void CheckWrongSecretAndDisposal()
    {
        var time = new ManualTimeProvider();
        using var client = CreateSession(SecureUdpPeerRole.Client, time);
        var wrongSecret =
            (byte[])SecureUdpProtectedTestData.BindingSecret.Clone();
        wrongSecret[0] ^= 0x80;
        using var wrongServer = new SecureUdpProtectedSession(
            SecureUdpPeerRole.Server,
            wrongSecret,
            SecureUdpProtectedTestData.ConnectionId,
            SecureUdpProtectedTestData.ServerId,
            TimeSpan.FromSeconds(5),
            time);
        var packet = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(70));
        var plaintext = new byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        Check.True(
            !wrongServer.TryUnprotect(
                packet,
                plaintext,
                out _,
                out _,
                out var error) &&
            error == SecureUdpProtectedError.AuthenticationFailed,
            "different TLS binding secret cannot decrypt");

        var disposable = CreateSession(
            SecureUdpPeerRole.Client,
            time);
        disposable.Dispose();
        Check.True(
            !disposable.TryProtect(
                SecureUdpProtectedMessageType.Ping,
                SecureUdpProtectedTestData.CreatePing(),
                new byte[
                    SecureUdpProtectedConstants.MaximumDatagramBytes],
                out _,
                out error) &&
            error == SecureUdpProtectedError.Disposed,
            "disposed protected session fails closed");
        Check.Throws<ObjectDisposedException>(
            () => disposable.GetSnapshot(),
            "disposed protected session hides state");
        CryptographicOperations.ZeroMemory(wrongSecret);
    }

    private static byte[] EncryptRaw(
        SecureUdpProtectedMessageType messageType,
        byte[] payload,
        SecureUdpTrafficDirection direction,
        uint keyEpoch)
    {
        var key = SecureUdpProtectedTestData.DeriveKey(
            direction,
            keyEpoch);
        var output = new byte[
            SecureUdpProtectedConstants.MaximumDatagramBytes];
        Check.True(
            SecureUdpProtectedCodec.TryEncrypt(
                SecureUdpProtectedTestData.CreateHeader(
                    messageType,
                    payload.Length,
                    keyEpoch),
                key,
                payload,
                output,
                out var written,
                out _),
            "raw protected test packet encrypts");
        CryptographicOperations.ZeroMemory(key);
        return output[..written];
    }

    private static byte[] EncryptRawContentUnchecked(
        byte[] payload)
    {
        var header = SecureUdpProtectedTestData.CreateHeader(
            SecureUdpProtectedMessageType.Ping,
            payload.Length);
        var output = new byte[header.DatagramBytes];
        Check.True(
            SecureUdpProtectedCodec.TryWriteHeader(header, output),
            "invalid-content raw header writes");
        var key = SecureUdpProtectedTestData.DeriveKey(
            SecureUdpTrafficDirection.ClientToServer);
        Span<byte> nonce = stackalloc byte[
            SecureUdpProtectedConstants.NonceBytes];
        BinaryPrimitives.WriteUInt32BigEndian(nonce, 1);
        using (var aes = new AesGcm(
            key,
            SecureUdpProtectedConstants.TagBytes))
        {
            aes.Encrypt(
                nonce,
                payload,
                output.AsSpan(64, payload.Length),
                output.AsSpan(64 + payload.Length, 16),
                output.AsSpan(0, 64));
        }
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(nonce);
        return output;
    }
}
