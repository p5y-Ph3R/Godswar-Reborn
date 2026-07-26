using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure.Realtime;

[Flags]
internal enum SecureRealtimeMovementFlags : byte
{
    None = 0,
    CurrentWorld = 1
}

[Flags]
internal enum SecureRealtimeSnapshotFlags : byte
{
    None = 0,
    Keyframe = 1,
    Correction = 2
}

internal enum SecureRealtimeTransportSource : byte
{
    Tls = 1,
    Udp = 2
}

internal enum SecureRealtimeMovementIngressKind : byte
{
    Input = 1,
    TransportTransition = 2
}

internal enum SecureRealtimeMovementRejection : byte
{
    None = 0,
    Malformed = 1,
    NotReady = 2,
    Dead = 3,
    InvalidCoordinates = 4,
    MapTransition = 5,
    Cadence = 6,
    Speed = 7,
    Distance = 8,
    StaleInput = 9,
    TransportEpoch = 10,
    TransportSource = 11,
    Overloaded = 12
}

internal readonly record struct SecureRealtimeMovementInput(
    SecureRealtimeMovementFlags Flags,
    uint TransportEpoch,
    ulong InputId,
    ulong ClientMonotonicMilliseconds,
    uint WorldGeneration,
    uint LegacyState,
    float X,
    float Z,
    float Auxiliary,
    byte MapId);

internal readonly record struct SecureRealtimeMovementIngress(
    SecureRealtimeMovementInput Input,
    SecureRealtimeTransportSource TransportSource,
    TimeSpan ServerReceiveElapsed,
    SecureRealtimeMovementIngressKind Kind);

internal readonly record struct SecureRealtimePositionSnapshot(
    SecureRealtimeSnapshotFlags Flags,
    uint TransportEpoch,
    ulong AcknowledgedInputId,
    ulong ServerTick,
    ulong PositionRevision,
    ulong SnapshotSequence,
    uint WorldGeneration,
    uint LegacyState,
    float X,
    float Z,
    float Auxiliary,
    byte MapId,
    SecureRealtimeMovementRejection Rejection);

internal static class SecureRealtimeMovementProtocol
{
    public const byte Version = 1;
    public const int MovementInputBytes = 52;
    public const int PositionSnapshotBytes = 64;
    public const ushort LegacyWalkOpcode = 10194;
    public const ushort LegacyWalkBytes = 20;

    private const SecureRealtimeMovementFlags KnownMovementFlags =
        SecureRealtimeMovementFlags.CurrentWorld;
    private const SecureRealtimeSnapshotFlags KnownSnapshotFlags =
        SecureRealtimeSnapshotFlags.Keyframe |
        SecureRealtimeSnapshotFlags.Correction;

    public static bool TryEncodeMovementInput(
        in SecureRealtimeMovementInput input,
        SecureRealtimeTransportSource source,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.Length < MovementInputBytes ||
            !IsValid(input, source))
        {
            return false;
        }

        var output = destination[..MovementInputBytes];
        output.Clear();
        output[0] = Version;
        output[1] = (byte)input.Flags;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[2..],
            checked((ushort)MovementInputBytes));
        BinaryPrimitives.WriteUInt32BigEndian(
            output[4..],
            input.TransportEpoch);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[8..],
            input.InputId);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[16..],
            input.ClientMonotonicMilliseconds);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[24..],
            input.WorldGeneration);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[28..],
            input.LegacyState);
        WriteSingle(output[32..], input.X);
        WriteSingle(output[36..], input.Z);
        WriteSingle(output[40..], input.Auxiliary);
        output[44] = input.MapId;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[48..],
            LegacyWalkOpcode);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[50..],
            LegacyWalkBytes);
        bytesWritten = MovementInputBytes;
        return true;
    }

    public static bool TryDecodeMovementInput(
        ReadOnlySpan<byte> source,
        SecureRealtimeTransportSource transportSource,
        out SecureRealtimeMovementInput input)
    {
        input = default;
        if (!IsTransportSource(transportSource) ||
            source.Length != MovementInputBytes ||
            source[0] != Version ||
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]) !=
                MovementInputBytes ||
            source[45] != 0 ||
            source[46] != 0 ||
            source[47] != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(source[48..]) !=
                LegacyWalkOpcode ||
            BinaryPrimitives.ReadUInt16BigEndian(source[50..]) !=
                LegacyWalkBytes)
        {
            return false;
        }

        var candidate = new SecureRealtimeMovementInput(
            (SecureRealtimeMovementFlags)source[1],
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[24..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[28..]),
            ReadSingle(source[32..]),
            ReadSingle(source[36..]),
            ReadSingle(source[40..]),
            source[44]);
        if (!IsValid(candidate, transportSource))
        {
            return false;
        }

        input = candidate;
        return true;
    }

    public static bool TryEncodePositionSnapshot(
        in SecureRealtimePositionSnapshot snapshot,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.Length < PositionSnapshotBytes ||
            !IsValid(snapshot))
        {
            return false;
        }

        var output = destination[..PositionSnapshotBytes];
        output.Clear();
        output[0] = Version;
        output[1] = (byte)snapshot.Flags;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[2..],
            checked((ushort)PositionSnapshotBytes));
        BinaryPrimitives.WriteUInt32BigEndian(
            output[4..],
            snapshot.TransportEpoch);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[8..],
            snapshot.AcknowledgedInputId);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[16..],
            snapshot.ServerTick);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[24..],
            snapshot.PositionRevision);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[32..],
            snapshot.SnapshotSequence);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[40..],
            snapshot.WorldGeneration);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[44..],
            snapshot.LegacyState);
        WriteSingle(output[48..], snapshot.X);
        WriteSingle(output[52..], snapshot.Z);
        WriteSingle(output[56..], snapshot.Auxiliary);
        output[60] = snapshot.MapId;
        output[61] = (byte)snapshot.Rejection;
        bytesWritten = PositionSnapshotBytes;
        return true;
    }

    public static bool TryDecodePositionSnapshot(
        ReadOnlySpan<byte> source,
        out SecureRealtimePositionSnapshot snapshot)
    {
        snapshot = default;
        if (source.Length != PositionSnapshotBytes ||
            source[0] != Version ||
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]) !=
                PositionSnapshotBytes ||
            source[62] != 0 ||
            source[63] != 0)
        {
            return false;
        }

        var candidate = new SecureRealtimePositionSnapshot(
            (SecureRealtimeSnapshotFlags)source[1],
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[24..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[32..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[40..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[44..]),
            ReadSingle(source[48..]),
            ReadSingle(source[52..]),
            ReadSingle(source[56..]),
            source[60],
            (SecureRealtimeMovementRejection)source[61]);
        if (!IsValid(candidate))
        {
            return false;
        }

        snapshot = candidate;
        return true;
    }

    public static bool IsValid(
        in SecureRealtimeMovementInput input,
        SecureRealtimeTransportSource source)
    {
        return IsTransportSource(source) &&
            (input.Flags & ~KnownMovementFlags) == 0 &&
            (source != SecureRealtimeTransportSource.Udp ||
                (input.Flags &
                    SecureRealtimeMovementFlags.CurrentWorld) == 0) &&
            input.TransportEpoch != 0 &&
            input.InputId != 0 &&
            input.ClientMonotonicMilliseconds != 0 &&
            IsFinite(input.X) &&
            IsFinite(input.Z) &&
            IsFinite(input.Auxiliary);
    }

    public static bool IsValid(
        in SecureRealtimePositionSnapshot snapshot)
    {
        var rejectionIsKnown =
            snapshot.Rejection >=
                SecureRealtimeMovementRejection.None &&
            snapshot.Rejection <=
                SecureRealtimeMovementRejection.Overloaded;
        var isCorrection =
            (snapshot.Flags &
                SecureRealtimeSnapshotFlags.Correction) != 0;
        return (snapshot.Flags & ~KnownSnapshotFlags) == 0 &&
            snapshot.TransportEpoch != 0 &&
            snapshot.ServerTick != 0 &&
            snapshot.SnapshotSequence != 0 &&
            rejectionIsKnown &&
            (snapshot.Rejection ==
                SecureRealtimeMovementRejection.None ||
                isCorrection) &&
            IsFinite(snapshot.X) &&
            IsFinite(snapshot.Z) &&
            IsFinite(snapshot.Auxiliary);
    }

    private static bool IsTransportSource(
        SecureRealtimeTransportSource source) =>
        source is SecureRealtimeTransportSource.Tls or
            SecureRealtimeTransportSource.Udp;

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    private static void WriteSingle(Span<byte> destination, float value)
    {
        BinaryPrimitives.WriteInt32BigEndian(
            destination,
            BitConverter.SingleToInt32Bits(value));
    }

    private static float ReadSingle(ReadOnlySpan<byte> source)
    {
        return BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32BigEndian(source));
    }
}
