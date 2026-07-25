using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureUdpProtectedSessionChecks
{
    public static void Run()
    {
        CheckBidirectionalControlAndAcknowledgements();
        CheckReplayReorderingAndUnauthenticatedMutation();
        CheckEpochPromotionOverlapAndRotationPolicy();
        CheckDirectionAndPayloadPolicy();
        CheckWrongSecretAndDisposal();
    }

    private static void CheckBidirectionalControlAndAcknowledgements()
    {
        var time = new ManualTimeProvider();
        using var client = CreateSession(SecureUdpPeerRole.Client, time);
        using var server = CreateSession(SecureUdpPeerRole.Server, time);

        var ping = SecureUdpProtectedTestData.CreatePing();
        var clientPacket = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            ping);
        Check.True(
            SecureUdpProtectedCodec.TryDecodeHeader(
                clientPacket,
                out var sentHeader),
            "client Ping header decodes");
        Check.Equal(1u, sentHeader.KeyEpoch, "initial client epoch");
        Check.Equal(0UL, sentHeader.Sequence, "initial client sequence");
        Check.True(
            sentHeader.Acknowledgement ==
                SecureUdpAcknowledgement.None,
            "first client packet has no acknowledgement");
        CheckUnprotect(
            server,
            clientPacket,
            ping,
            "server receives protected Ping");

        var pong = SecureUdpProtectedTestData.CreatePong();
        var serverPacket = Protect(
            server,
            SecureUdpProtectedMessageType.Pong,
            pong);
        Check.True(
            SecureUdpProtectedCodec.TryDecodeHeader(
                serverPacket,
                out sentHeader),
            "server Pong header decodes");
        Check.Equal(
            new SecureUdpAcknowledgement(1, 0, 0),
            sentHeader.Acknowledgement,
            "server automatically acknowledges client sequence zero");
        CheckUnprotect(
            client,
            serverPacket,
            pong,
            "client receives protected Pong");

        var confirmation =
            SecureUdpProtectedTestData.CreateBindingConfirm();
        serverPacket = Protect(
            server,
            SecureUdpProtectedMessageType.BindingConfirm,
            confirmation);
        CheckUnprotect(
            client,
            serverPacket,
            confirmation,
            "client receives protected binding confirmation");

        clientPacket = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(2));
        Check.True(
            SecureUdpProtectedCodec.TryDecodeHeader(
                clientPacket,
                out sentHeader),
            "second client Ping header decodes");
        Check.Equal(
            new SecureUdpAcknowledgement(1, 1, 1),
            sentHeader.Acknowledgement,
            "client ACK includes server high-water and prior packet");
        CheckUnprotect(
            server,
            clientPacket,
            SecureUdpProtectedTestData.CreatePing(2),
            "server receives second protected Ping");

        var snapshot = server.GetSnapshot();
        Check.True(
            snapshot.HasReceivedCurrentEpoch &&
            snapshot.HighestReceivedSequence == 1 &&
            snapshot.ReceiveReplayBitsLow == 3,
            "server snapshot exposes bounded replay state");
    }

    private static void CheckReplayReorderingAndUnauthenticatedMutation()
    {
        var time = new ManualTimeProvider();
        using var client = CreateSession(SecureUdpPeerRole.Client, time);
        using var server = CreateSession(SecureUdpPeerRole.Server, time);
        var packet0 = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(10));
        var packet1 = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(11));
        var packet2 = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(12));

        CheckUnprotect(
            server,
            packet2,
            SecureUdpProtectedTestData.CreatePing(12),
            "latest reordered Ping");
        CheckUnprotect(
            server,
            packet0,
            SecureUdpProtectedTestData.CreatePing(10),
            "older reordered Ping");
        CheckUnprotect(
            server,
            packet1,
            SecureUdpProtectedTestData.CreatePing(11),
            "middle reordered Ping");

        var plaintext = Enumerable.Repeat(
                (byte)0xCC,
                SecureUdpProtectedConstants.MaximumPayloadBytes)
            .ToArray();
        Check.True(
            !server.TryUnprotect(
                packet1,
                plaintext,
                out _,
                out var payloadBytes,
                out var error) &&
            error == SecureUdpProtectedError.ReplayRejected &&
            payloadBytes == 0,
            "duplicate protected sequence rejects");

        using var mutationClient =
            CreateSession(SecureUdpPeerRole.Client, time);
        using var mutationServer =
            CreateSession(SecureUdpPeerRole.Server, time);
        var original = Protect(
            mutationClient,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(20));
        var tampered = (byte[])original.Clone();
        tampered[39] ^= 1;
        Check.True(
            !mutationServer.TryUnprotect(
                tampered,
                plaintext,
                out _,
                out _,
                out error) &&
            error == SecureUdpProtectedError.AuthenticationFailed,
            "forged high sequence fails AEAD");
        var snapshot = mutationServer.GetSnapshot();
        Check.True(
            !snapshot.HasReceivedCurrentEpoch,
            "failed AEAD cannot advance replay high-water");
        CheckUnprotect(
            mutationServer,
            original,
            SecureUdpProtectedTestData.CreatePing(20),
            "original packet remains acceptable after forgery");
    }

    private static void CheckEpochPromotionOverlapAndRotationPolicy()
    {
        var time = new ManualTimeProvider();
        using var client = CreateSession(
            SecureUdpPeerRole.Client,
            time,
            TimeSpan.FromSeconds(2));
        using var server = CreateSession(
            SecureUdpPeerRole.Server,
            time,
            TimeSpan.FromSeconds(2));
        var oldPacket0 = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(30));
        var oldPacket1 = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(31));

        Check.True(
            client.TryRotateSendEpoch(out var error) &&
            error == SecureUdpProtectedError.None,
            "client send epoch rotates");
        var newPacket = Protect(
            client,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(32));
        CheckUnprotect(
            server,
            newPacket,
            SecureUdpProtectedTestData.CreatePing(32),
            "next authenticated epoch promotes");
        var snapshot = server.GetSnapshot();
        Check.True(
            snapshot.ReceiveKeyEpoch == 2 &&
            snapshot.PreviousReceiveKeyEpoch == 1,
            "receiver retains one previous epoch");
        CheckUnprotect(
            server,
            oldPacket0,
            SecureUdpProtectedTestData.CreatePing(30),
            "previous epoch accepted during overlap");

        time.Advance(TimeSpan.FromSeconds(2));
        var plaintext = new byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        Check.True(
            !server.TryUnprotect(
                oldPacket1,
                plaintext,
                out _,
                out _,
                out error) &&
            error == SecureUdpProtectedError.UnknownKeyEpoch,
            "previous epoch expires at exact overlap boundary");
        snapshot = server.GetSnapshot();
        Check.Equal(
            0u,
            snapshot.PreviousReceiveKeyEpoch,
            "expired previous key is removed");

        using var forgedClient =
            CreateSession(SecureUdpPeerRole.Client, time);
        using var forgedServer =
            CreateSession(SecureUdpPeerRole.Server, time);
        Check.True(
            forgedClient.TryRotateSendEpoch(out _),
            "forged-next fixture rotates");
        var forgedNext = Protect(
            forgedClient,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(40));
        forgedNext[^1] ^= 1;
        Check.True(
            !forgedServer.TryUnprotect(
                forgedNext,
                plaintext,
                out _,
                out _,
                out error) &&
            error == SecureUdpProtectedError.AuthenticationFailed,
            "forged next-epoch packet fails authentication");
        Check.Equal(
            1u,
            forgedServer.GetSnapshot().ReceiveKeyEpoch,
            "failed next epoch cannot promote receive keys");

        using var skipClient =
            CreateSession(SecureUdpPeerRole.Client, time);
        using var skipServer =
            CreateSession(SecureUdpPeerRole.Server, time);
        Check.True(
            skipClient.TryRotateSendEpoch(out _) &&
            skipClient.TryRotateSendEpoch(out _),
            "skip-epoch fixture reaches epoch three");
        var skipped = Protect(
            skipClient,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(50));
        Check.True(
            !skipServer.TryUnprotect(
                skipped,
                plaintext,
                out _,
                out _,
                out error) &&
            error == SecureUdpProtectedError.UnknownKeyEpoch,
            "receiver rejects epochs beyond exactly next");

        using var ageRotated =
            CreateSession(SecureUdpPeerRole.Server, time);
        Check.True(
            ageRotated.RotateSendEpochIfDue(
                100,
                TimeSpan.FromSeconds(5)) ==
                    SecureUdpKeyRotationStatus.NotDue,
            "key rotation not due initially");
        time.Advance(TimeSpan.FromSeconds(5));
        Check.True(
            ageRotated.RotateSendEpochIfDue(
                100,
                TimeSpan.FromSeconds(5)) ==
                    SecureUdpKeyRotationStatus.Rotated,
            "key rotation occurs at age boundary");
        snapshot = ageRotated.GetSnapshot();
        Check.True(
            snapshot.SendKeyEpoch == 2 &&
            snapshot.NextSendSequence == 0,
            "rotated send epoch resets sequence only with new key");

        using var packetRotated =
            CreateSession(SecureUdpPeerRole.Client, time);
        _ = Protect(
            packetRotated,
            SecureUdpProtectedMessageType.Ping,
            SecureUdpProtectedTestData.CreatePing(60));
        Check.True(
            packetRotated.RotateSendEpochIfDue(
                1,
                TimeSpan.FromHours(1)) ==
                    SecureUdpKeyRotationStatus.Rotated,
            "packet limit rotates send key");
    }

    private static SecureUdpProtectedSession CreateSession(
        SecureUdpPeerRole role,
        TimeProvider timeProvider,
        TimeSpan? overlap = null)
    {
        return new SecureUdpProtectedSession(
            role,
            SecureUdpProtectedTestData.BindingSecret,
            SecureUdpProtectedTestData.ConnectionId,
            SecureUdpProtectedTestData.ServerId,
            overlap ?? TimeSpan.FromSeconds(5),
            timeProvider);
    }

    private static byte[] Protect(
        SecureUdpProtectedSession session,
        SecureUdpProtectedMessageType type,
        byte[] payload)
    {
        var output = new byte[
            SecureUdpProtectedConstants.MaximumDatagramBytes];
        Check.True(
            session.TryProtect(
                type,
                payload,
                output,
                out var written,
                out var error),
            $"protected session encrypts {type} ({error})");
        return output[..written];
    }

    private static void CheckUnprotect(
        SecureUdpProtectedSession session,
        byte[] datagram,
        byte[] expectedPayload,
        string description)
    {
        var output = new byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        Check.True(
            session.TryUnprotect(
                datagram,
                output,
                out _,
                out var payloadBytes,
                out var error),
            $"{description} ({error})");
        Check.True(
            output.AsSpan(0, payloadBytes).SequenceEqual(
                expectedPayload),
            $"{description} payload");
    }

}
