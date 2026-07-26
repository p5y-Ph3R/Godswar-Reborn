using System.Buffers.Binary;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeMovementProtocolChecks
{
    public static Task RunAsync()
    {
        CheckMovementGoldenVector();
        CheckMovementValidation();
        CheckSnapshotGoldenVector();
        CheckSnapshotValidation();
        CheckCapacityOneMailboxes();
        CheckTransportReconciliation();
        CheckProtectedDirections();
        CheckTlsFrameContract();
        CheckGrantCapability();
        CheckFeatureDefaultsAndDatagramBudget();
        return Task.CompletedTask;
    }

    private static void CheckMovementGoldenVector()
    {
        var input = CreateInput(
            SecureRealtimeMovementFlags.CurrentWorld,
            epoch: 0x01020304,
            inputId: 0x0102030405060708,
            clientMilliseconds: 0x1112131415161718);
        var payload = new byte[
            SecureRealtimeMovementProtocol.MovementInputBytes];
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                input,
                SecureRealtimeTransportSource.Tls,
                payload,
                out var written) &&
            written == payload.Length,
            "realtime movement golden vector encodes");
        Check.True(
            payload[0] == 1 &&
            payload[1] == 1 &&
            BinaryPrimitives.ReadUInt16BigEndian(payload[2..]) == 52 &&
            BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) ==
                0x01020304 &&
            BinaryPrimitives.ReadUInt64BigEndian(payload[8..]) ==
                0x0102030405060708 &&
            BinaryPrimitives.ReadUInt64BigEndian(payload[16..]) ==
                0x1112131415161718 &&
            BinaryPrimitives.ReadUInt32BigEndian(payload[24..]) ==
                input.WorldGeneration &&
            BinaryPrimitives.ReadUInt32BigEndian(payload[28..]) ==
                input.LegacyState &&
            payload[44] == input.MapId &&
            payload[45] == 0 &&
            payload[46] == 0 &&
            payload[47] == 0 &&
            BinaryPrimitives.ReadUInt16BigEndian(payload[48..]) ==
                10194 &&
            BinaryPrimitives.ReadUInt16BigEndian(payload[50..]) ==
                20,
            "realtime movement uses the locked big-endian layout");
        Check.True(
            SecureRealtimeMovementProtocol.TryDecodeMovementInput(
                payload,
                SecureRealtimeTransportSource.Tls,
                out var decoded) &&
            decoded == input,
            "realtime movement golden vector round trips");
    }

    private static void CheckMovementValidation()
    {
        var input = CreateInput();
        var payload = new byte[52];
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                input,
                SecureRealtimeTransportSource.Udp,
                payload,
                out _),
            "valid UDP movement input encodes");

        var currentWorld = input with
        {
            Flags = SecureRealtimeMovementFlags.CurrentWorld
        };
        Check.True(
            !SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                currentWorld,
                SecureRealtimeTransportSource.Udp,
                payload,
                out _) &&
            SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                currentWorld,
                SecureRealtimeTransportSource.Tls,
                payload,
                out _),
            "CurrentWorld is negotiated only on TLS fallback");

        foreach (var invalid in new[]
        {
            input with { TransportEpoch = 0 },
            input with { InputId = 0 },
            input with { ClientMonotonicMilliseconds = 0 },
            input with { X = float.NaN },
            input with { Z = float.PositiveInfinity },
            input with { Auxiliary = float.NegativeInfinity },
            input with
            {
                Flags = (SecureRealtimeMovementFlags)0x80
            }
        })
        {
            Check.True(
                !SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                    invalid,
                    SecureRealtimeTransportSource.Udp,
                    payload,
                    out _),
                "invalid realtime movement is rejected before encoding");
        }

        Check.True(
            SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                input,
                SecureRealtimeTransportSource.Udp,
                payload,
                out _),
            "movement mutation baseline encodes");
        foreach (var offset in new[] { 0, 2, 45, 48, 50 })
        {
            var malformed = payload.ToArray();
            malformed[offset] ^= 0x40;
            Check.True(
                !SecureRealtimeMovementProtocol.TryDecodeMovementInput(
                    malformed,
                    SecureRealtimeTransportSource.Udp,
                    out _),
                $"movement mutation at offset {offset} rejects");
        }
    }

    private static void CheckSnapshotGoldenVector()
    {
        var snapshot = CreateSnapshot(
            SecureRealtimeSnapshotFlags.Keyframe |
                SecureRealtimeSnapshotFlags.Correction,
            SecureRealtimeMovementRejection.Distance);
        var payload = new byte[64];
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodePositionSnapshot(
                snapshot,
                payload,
                out var written) &&
            written == payload.Length &&
            payload[0] == 1 &&
            payload[1] == 3 &&
            BinaryPrimitives.ReadUInt16BigEndian(payload[2..]) == 64 &&
            BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) ==
                snapshot.TransportEpoch &&
            BinaryPrimitives.ReadUInt64BigEndian(payload[8..]) ==
                snapshot.AcknowledgedInputId &&
            BinaryPrimitives.ReadUInt64BigEndian(payload[16..]) ==
                snapshot.ServerTick &&
            BinaryPrimitives.ReadUInt64BigEndian(payload[24..]) ==
                snapshot.PositionRevision &&
            BinaryPrimitives.ReadUInt64BigEndian(payload[32..]) ==
                snapshot.SnapshotSequence &&
            payload[60] == snapshot.MapId &&
            payload[61] == (byte)snapshot.Rejection &&
            payload[62] == 0 &&
            payload[63] == 0,
            "position snapshot uses the locked big-endian layout");
        Check.True(
            SecureRealtimeMovementProtocol.TryDecodePositionSnapshot(
                payload,
                out var decoded) &&
            decoded == snapshot,
            "position snapshot golden vector round trips");

        var initial = snapshot with
        {
            Flags = SecureRealtimeSnapshotFlags.Keyframe,
            AcknowledgedInputId = 0,
            PositionRevision = 0,
            Rejection = SecureRealtimeMovementRejection.None
        };
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodePositionSnapshot(
                initial,
                payload,
                out _),
            "initial keyframe permits zero acknowledgement and revision");
    }

    private static void CheckSnapshotValidation()
    {
        var baseline = CreateSnapshot(
            SecureRealtimeSnapshotFlags.None,
            SecureRealtimeMovementRejection.None);
        var payload = new byte[64];
        foreach (var invalid in new[]
        {
            baseline with { TransportEpoch = 0 },
            baseline with { ServerTick = 0 },
            baseline with { SnapshotSequence = 0 },
            baseline with { X = float.NaN },
            baseline with
            {
                Flags = (SecureRealtimeSnapshotFlags)0x80
            },
            baseline with
            {
                Rejection = SecureRealtimeMovementRejection.Speed
            },
            baseline with
            {
                Flags = SecureRealtimeSnapshotFlags.Correction,
                Rejection =
                    (SecureRealtimeMovementRejection)byte.MaxValue
            }
        })
        {
            Check.True(
                !SecureRealtimeMovementProtocol
                    .TryEncodePositionSnapshot(
                        invalid,
                        payload,
                        out _),
                "invalid position snapshot rejects before encoding");
        }

        var correction = baseline with
        {
            Flags = SecureRealtimeSnapshotFlags.Correction
        };
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodePositionSnapshot(
                correction,
                payload,
                out _),
            "correction reason zero remains valid");
        payload[62] = 1;
        Check.True(
            !SecureRealtimeMovementProtocol.TryDecodePositionSnapshot(
                payload,
                out _),
            "snapshot reserved bytes reject");
    }

    private static void CheckCapacityOneMailboxes()
    {
        using var mailbox =
            new SecureRealtimeSingleSlot<SecureRealtimeMovementInput>();
        var first = CreateInput(inputId: 1);
        var latest = CreateInput(inputId: 2);
        Check.True(
            mailbox.Offer(first) ==
                SecureRealtimeMailboxOfferStatus.Accepted &&
            mailbox.Offer(latest) ==
                SecureRealtimeMailboxOfferStatus.Replaced &&
            mailbox.GetSnapshot() is
            {
                HasItem: true,
                Accepted: 2,
                Replaced: 1,
                Taken: 0
            } &&
            mailbox.TryTake(out var taken) &&
            taken == latest &&
            !mailbox.TryTake(out _),
            "capacity-one mailbox replaces stale work");
        mailbox.Dispose();
        Check.True(
            mailbox.Offer(first) ==
                SecureRealtimeMailboxOfferStatus.Disposed,
            "disposed realtime mailbox never accepts work");
        Check.True(
            SecureRealtimeSingleSlot<int>.IncrementSaturating(0) == 1 &&
            SecureRealtimeSingleSlot<int>.IncrementSaturating(
                ulong.MaxValue) == ulong.MaxValue,
            "externally driven mailbox counters saturate without throwing");
    }

    private static void CheckProtectedDirections()
    {
        using var client = CreateProtected(
            SecureUdpPeerRole.Client);
        using var server = CreateProtected(
            SecureUdpPeerRole.Server);
        var movement = new byte[52];
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                CreateInput(),
                SecureRealtimeTransportSource.Udp,
                movement,
                out _),
            "protected movement fixture encodes");
        var datagram = new byte[1_200];
        Check.True(
            client.TryProtect(
                SecureUdpProtectedMessageType.MovementInput,
                movement,
                datagram,
                out var datagramBytes,
                out _) &&
            server.TryUnprotect(
                datagram.AsSpan(0, datagramBytes),
                new byte[1_120],
                out var header,
                out var payloadBytes,
                out _) &&
            header.MessageType ==
                SecureUdpProtectedMessageType.MovementInput &&
            payloadBytes == 52,
            "protected movement is client-to-server only");

        var snapshot = new byte[64];
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodePositionSnapshot(
                CreateSnapshot(
                    SecureRealtimeSnapshotFlags.Keyframe,
                    SecureRealtimeMovementRejection.None),
                snapshot,
                out _) &&
            server.TryProtect(
                SecureUdpProtectedMessageType.PositionSnapshot,
                snapshot,
                datagram,
                out _,
                out _) &&
            !client.TryProtect(
                SecureUdpProtectedMessageType.PositionSnapshot,
                snapshot,
                datagram,
                out _,
                out var directionError) &&
            directionError ==
                SecureUdpProtectedError.InvalidMessageDirection,
            "protected snapshots are server-to-client only");
    }

    private static void CheckTlsFrameContract()
    {
        Span<byte> header = stackalloc byte[16];
        var frame = new SecureFrameHeader(
            52,
            SecureFrameType.RealtimeMovementInput,
            1);
        Check.True(
            SecureFrameCodec.TryEncodeHeader(
                frame,
                SecureEndpointRole.Game,
                SecureFrameDirection.ClientToServer,
                header) &&
            SecureFrameCodec.TryDecodeHeader(
                header,
                SecureEndpointRole.Game,
                SecureFrameDirection.ClientToServer,
                1,
                out var decoded) &&
            decoded == frame &&
            !SecureFrameCodec.TryEncodeHeader(
                frame,
                SecureEndpointRole.Login,
                SecureFrameDirection.ClientToServer,
                header) &&
            !SecureFrameCodec.TryEncodeHeader(
                frame with { PayloadLength = 51 },
                SecureEndpointRole.Game,
                SecureFrameDirection.ClientToServer,
                header),
            "TLS fallback frame is exact-length game C2S traffic");
    }

    private static void CheckGrantCapability()
    {
        using var grant = new SecureUdpBindingGrant(
            7_444,
            100,
            1_700_000_000_000,
            SecureUdpProtectedTestData.ConnectionId,
            SecureUdpProtectedTestData.BindingSecret,
            SecureUdpBindingCapabilities.AuthoritativeMovement);
        var payload = new byte[72];
        Check.True(
            SecureUdpBindingGrantCodec.TryEncode(
                grant,
                payload,
                out var written) &&
            written == payload.Length &&
            BinaryPrimitives.ReadUInt16BigEndian(payload[10..]) == 1,
            "UDP grant advertises the movement capability bit");
        Check.True(
            SecureUdpBindingGrantCodec.TryDecode(
                payload,
                out var decoded),
            "UDP movement grant decodes");
        Check.True(
            decoded!.Capabilities ==
                SecureUdpBindingCapabilities.AuthoritativeMovement,
            "UDP grant advertises the known movement capability");
        decoded!.Dispose();
        payload[10] = 0x80;
        Check.True(
            !SecureUdpBindingGrantCodec.TryDecode(
                payload,
                out _),
            "UDP grant rejects unknown capability bits");
    }

    internal static SecureRealtimeMovementInput CreateInput(
        SecureRealtimeMovementFlags flags =
            SecureRealtimeMovementFlags.None,
        uint epoch = 1,
        ulong inputId = 1,
        ulong clientMilliseconds = 10_000) =>
        new(
            flags,
            epoch,
            inputId,
            clientMilliseconds,
            WorldGeneration: 17,
            LegacyState: 0x00020004,
            X: 101.25f,
            Z: -202.5f,
            Auxiliary: 1.5f,
            MapId: 2);

    internal static SecureRealtimePositionSnapshot CreateSnapshot(
        SecureRealtimeSnapshotFlags flags,
        SecureRealtimeMovementRejection rejection) =>
        new(
            flags,
            TransportEpoch: 1,
            AcknowledgedInputId: 1,
            ServerTick: 101,
            PositionRevision: 9,
            SnapshotSequence: 11,
            WorldGeneration: 17,
            LegacyState: 0x00020004,
            X: 101.25f,
            Z: -202.5f,
            Auxiliary: 1.5f,
            MapId: 2,
            rejection);

    private static SecureUdpProtectedSession CreateProtected(
        SecureUdpPeerRole role) =>
        new(
            role,
            SecureUdpProtectedTestData.BindingSecret,
            SecureUdpProtectedTestData.ConnectionId,
            SecureUdpProtectedTestData.ServerId,
            TimeSpan.FromSeconds(10));
}
