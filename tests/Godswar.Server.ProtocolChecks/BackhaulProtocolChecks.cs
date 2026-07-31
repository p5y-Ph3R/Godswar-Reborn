using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulProtocolChecks
{
    public const string CheckName =
        "B18C2 authenticated backhaul protocol and admission";

    private static readonly ServerNodeId WorkerNode =
        new("worker-a");
    private static readonly RealmId Realm = RealmId.Tempest;
    private static readonly MapId Map = new(4);
    private static readonly WorldInstanceId World =
        new(Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));

    public static async Task RunAsync()
    {
        CheckCodecGoldenVectorAndRoundTrips();
        CheckCodecBoundsAndMalformedFrames();
        CheckWorkerAdmissionLifecycle();
        CheckWorkerAdmissionPolicyAndExpiry();
        CheckWorkerAdmissionChurnAndReplayBounds();
        await CheckGatewayExactWorldJoinAsync();
        await CheckTlsPolicyAndRoundTripAsync();
    }

    private static void CheckCodecGoldenVectorAndRoundTrips()
    {
        var admission = Admission();
        var frame = new byte[
            BackhaulProtocolConstants.OpenSessionFrameBytes];
        Check.True(
            BackhaulCodec.TryEncodeOpenSession(
                admission,
                frame,
                out var written),
            "canonical open-session frame encodes");
        Check.Equal(
            frame.Length,
            written,
            "open-session encoder writes the fixed frame length");
        Check.Equal(
            "4757424800010001000000D3",
            Convert.ToHexString(frame.AsSpan(0, 12)),
            "open-session header is a stable network-order vector");
        Check.Equal(
            "00112233445566778899AABBCCDDEEFF",
            Convert.ToHexString(frame.AsSpan(12, 16)),
            "GUIDs use canonical big-endian byte order");
        Check.Equal(
            "A166E385E591EB420DFB85FBB22146C9ACF8F621B2CEC41805E58F6E4CE732C4",
            Convert.ToHexString(SHA256.HashData(frame)),
            "complete open-session wire vector remains stable");

        Check.True(
            BackhaulCodec.TryDecodeOpenSession(
                frame,
                out var decoded,
                out var failure),
            "canonical open-session frame decodes");
        Check.True(
            failure == BackhaulDecodeFailure.None &&
            decoded is not null,
            "valid frame has no decode failure");
        AssertAdmissionEqual(admission, decoded!);

        for (var index = 1; index <= 64; index++)
        {
            var source = index % 2 == 0
                ? new IPEndPoint(
                    IPAddress.Parse($"198.51.100.{index}"),
                    10_000 + index)
                : new IPEndPoint(
                    IPAddress.Parse($"2001:db8::{index:x}"),
                    10_000 + index);
            var value = Admission(
                connectionId: GuidFromInt(index),
                accountId: index,
                characterId: index * 2,
                username: $"USER{index}",
                source: source);
            Check.True(
                BackhaulCodec.TryEncodeOpenSession(
                    value,
                    frame,
                    out written) &&
                written == frame.Length &&
                BackhaulCodec.TryDecodeOpenSession(
                    frame,
                    out decoded,
                    out failure),
                $"property-style admission {index} round-trips");
            AssertAdmissionEqual(value, decoded!);
        }

        var responses = Enum.GetValues<BackhaulAdmissionStatus>();
        foreach (var status in responses)
        {
            var connectionId = status == BackhaulAdmissionStatus.Accepted
                ? admission.ConnectionId
                : Guid.Empty;
            var response = new BackhaulAdmissionResponse(
                status,
                connectionId);
            var responseFrame = new byte[
                BackhaulProtocolConstants.AdmissionResponseFrameBytes];
            var encoded =
                BackhaulCodec.TryEncodeAdmissionResponse(
                    response,
                    responseFrame,
                    out written);
            var decodedResponseOk =
                BackhaulCodec.TryDecodeAdmissionResponse(
                    responseFrame,
                    out var decodedResponse,
                    out failure);
            Check.True(
                encoded && decodedResponseOk,
                $"admission response {status} round-trips");
            Check.True(
                decodedResponse == response &&
                failure == BackhaulDecodeFailure.None,
                $"admission response {status} preserves values");
        }

        Check.True(
            BackhaulCodec.TryReadDeclaredFrameLength(
                frame.AsSpan(
                    0,
                    BackhaulProtocolConstants.HeaderBytes),
                out var declared) &&
            declared ==
                BackhaulProtocolConstants.OpenSessionFrameBytes,
            "bounded header parser derives the fixed frame length");
    }

    private static void CheckCodecBoundsAndMalformedFrames()
    {
        var admission = Admission();
        var undersized = new byte[
            BackhaulProtocolConstants.OpenSessionFrameBytes - 1];
        Check.True(
            !BackhaulCodec.TryEncodeOpenSession(
                admission,
                undersized,
                out var written) &&
            written == 0,
            "open-session encoder rejects a short destination");

        var valid = new byte[
            BackhaulProtocolConstants.OpenSessionFrameBytes];
        BackhaulCodec.TryEncodeOpenSession(
            admission,
            valid,
            out _);
        AssertOpenFailure(
            valid[..^1],
            BackhaulDecodeFailure.InvalidLength,
            "truncated frame");
        MutateAndAssert(
            valid,
            0,
            0,
            BackhaulDecodeFailure.InvalidMagic,
            "bad magic");
        MutateAndAssert(
            valid,
            5,
            2,
            BackhaulDecodeFailure.UnsupportedVersion,
            "unsupported version");
        MutateAndAssert(
            valid,
            7,
            2,
            BackhaulDecodeFailure.WrongMessageType,
            "wrong message type");
        MutateAndAssert(
            valid,
            11,
            1,
            BackhaulDecodeFailure.InvalidPayloadLength,
            "wrong payload length");
        MutateAndAssert(
            valid,
            106,
            5,
            BackhaulDecodeFailure.InvalidAdmission,
            "unknown address family");
        MutateAndAssert(
            valid,
            125,
            33,
            BackhaulDecodeFailure.InvalidAdmission,
            "oversized username");
        MutateAndAssert(
            valid,
            131,
            1,
            BackhaulDecodeFailure.InvalidAdmission,
            "nonzero username padding");
        var zeroPort = (byte[])valid.Clone();
        zeroPort[107] = 0;
        zeroPort[108] = 0;
        AssertOpenFailure(
            zeroPort,
            BackhaulDecodeFailure.InvalidAdmission,
            "zero source port");

        var random = new Random(1_984_021);
        for (var index = 0; index < 512; index++)
        {
            var bytes = new byte[
                random.Next(
                    0,
                    BackhaulProtocolConstants.MaximumFrameBytes + 2)];
            random.NextBytes(bytes);
            _ = BackhaulCodec.TryDecodeOpenSession(
                bytes,
                out _,
                out _);
            _ = BackhaulCodec.TryDecodeAdmissionResponse(
                bytes,
                out _,
                out _);
        }

        var responseFrame = new byte[
            BackhaulProtocolConstants.AdmissionResponseFrameBytes];
        BackhaulCodec.TryEncodeAdmissionResponse(
            new BackhaulAdmissionResponse(
                BackhaulAdmissionStatus.Accepted,
                admission.ConnectionId),
            responseFrame,
            out _);
        responseFrame[14] = 1;
        Check.True(
            !BackhaulCodec.TryDecodeAdmissionResponse(
                responseFrame,
                out _,
                out var responseFailure) &&
            responseFailure ==
                BackhaulDecodeFailure.InvalidReservedBytes,
            "nonzero response reserved bytes fail closed");
        responseFrame[14] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(
            responseFrame.AsSpan(12),
            ushort.MaxValue);
        Check.True(
            !BackhaulCodec.TryDecodeAdmissionResponse(
                responseFrame,
                out _,
                out responseFailure) &&
            responseFailure == BackhaulDecodeFailure.UnknownStatus,
            "unknown response status fails closed");

        var header = (byte[])valid[..12].Clone();
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(8),
            checked((uint)BackhaulProtocolConstants.MaximumFrameBytes));
        Check.True(
            !BackhaulCodec.TryReadDeclaredFrameLength(
                header,
                out _),
            "declared payload cannot exceed the fixed allocation bound");
    }

    private static GatewayWorldAdmission Admission(
        Guid? connectionId = null,
        int accountId = 7,
        int characterId = 13,
        string username = "TEST2",
        ServerNodeId? node = null,
        RealmId? realm = null,
        MapId? map = null,
        WorldInstanceId? world = null,
        DateTimeOffset? issued = null,
        DateTimeOffset? expires = null,
        IPEndPoint? source = null) =>
        new(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            connectionId ??
                Guid.Parse(
                    "11111111-2222-3333-4444-555555555555"),
            Guid.Parse(
                "66666666-7777-8888-9999-aaaaaaaaaaaa"),
            accountId,
            characterId,
            username,
            realm ?? Realm,
            map ?? Map,
            world ?? World,
            node ?? WorkerNode,
            issued ?? DateTimeOffset.UnixEpoch.AddSeconds(100),
            expires ?? DateTimeOffset.UnixEpoch.AddSeconds(160),
            source ??
                new IPEndPoint(
                    IPAddress.Parse("192.0.2.10"),
                    4242));

    private static Guid GuidFromInt(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32BigEndian(bytes[12..], value);
        return new Guid(bytes, bigEndian: true);
    }

    private static void AssertAdmissionEqual(
        GatewayWorldAdmission expected,
        GatewayWorldAdmission actual)
    {
        Check.True(
            expected.GatewayBootId == actual.GatewayBootId &&
            expected.ConnectionId == actual.ConnectionId &&
            expected.LoginGenerationId ==
                actual.LoginGenerationId &&
            expected.AccountId == actual.AccountId &&
            expected.CharacterId == actual.CharacterId &&
            expected.Username == actual.Username &&
            expected.RealmId == actual.RealmId &&
            expected.MapId == actual.MapId &&
            expected.WorldInstanceId == actual.WorldInstanceId &&
            expected.TargetNodeId == actual.TargetNodeId &&
            expected.IssuedAtUtc == actual.IssuedAtUtc &&
            expected.ExpiresAtUtc == actual.ExpiresAtUtc &&
            expected.ObservedClientSource.Equals(
                actual.ObservedClientSource),
            "decoded admission equals its authoritative input");
    }

    private static void MutateAndAssert(
        byte[] valid,
        int offset,
        byte value,
        BackhaulDecodeFailure expected,
        string description)
    {
        var mutated = (byte[])valid.Clone();
        mutated[offset] = value;
        AssertOpenFailure(mutated, expected, description);
    }

    private static void AssertOpenFailure(
        ReadOnlySpan<byte> frame,
        BackhaulDecodeFailure expected,
        string description)
    {
        Check.True(
            !BackhaulCodec.TryDecodeOpenSession(
                frame,
                out var decoded,
                out var actual) &&
            decoded is null &&
            actual == expected,
            $"{description} reports {expected}");
    }
}
