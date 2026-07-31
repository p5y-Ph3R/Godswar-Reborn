using System.Buffers.Binary;
using System.Net;
using System.Text;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Networking.Backhaul;

/// <summary>
/// Canonical network-byte-order codec. Both messages are fixed-size so a
/// peer cannot select allocation sizes or collection counts.
/// </summary>
internal static class BackhaulCodec
{
    private const int GatewayBootIdOffset = 12;
    private const int ConnectionIdOffset = 28;
    private const int LoginGenerationIdOffset = 44;
    private const int AccountIdOffset = 60;
    private const int CharacterIdOffset = 64;
    private const int RealmIdOffset = 68;
    private const int MapIdOffset = 72;
    private const int WorldInstanceIdOffset = 74;
    private const int IssuedAtOffset = 90;
    private const int ExpiresAtOffset = 98;
    private const int SourceFamilyOffset = 106;
    private const int SourcePortOffset = 107;
    private const int SourceAddressOffset = 109;
    private const int UsernameLengthOffset = 125;
    private const int UsernameOffset = 126;
    private const int NodeLengthOffset = 158;
    private const int NodeOffset = 159;

    private const int ResponseStatusOffset = 12;
    private const int ResponseReservedOffset = 14;
    private const int ResponseConnectionIdOffset = 16;

    public static bool TryEncodeOpenSession(
        GatewayWorldAdmission admission,
        Span<byte> destination,
        out int written)
    {
        ArgumentNullException.ThrowIfNull(admission);
        written = 0;
        if (destination.Length <
            BackhaulProtocolConstants.OpenSessionFrameBytes)
        {
            return false;
        }

        var frame = destination[
            ..BackhaulProtocolConstants.OpenSessionFrameBytes];
        frame.Clear();
        WriteHeader(
            frame,
            BackhaulMessageType.OpenSession,
            BackhaulProtocolConstants.OpenSessionPayloadBytes);
        WriteGuid(frame, GatewayBootIdOffset, admission.GatewayBootId);
        WriteGuid(frame, ConnectionIdOffset, admission.ConnectionId);
        WriteGuid(
            frame,
            LoginGenerationIdOffset,
            admission.LoginGenerationId);
        BinaryPrimitives.WriteInt32BigEndian(
            frame[AccountIdOffset..],
            admission.AccountId);
        BinaryPrimitives.WriteInt32BigEndian(
            frame[CharacterIdOffset..],
            admission.CharacterId);
        BinaryPrimitives.WriteInt32BigEndian(
            frame[RealmIdOffset..],
            admission.RealmId.Value);
        BinaryPrimitives.WriteInt16BigEndian(
            frame[MapIdOffset..],
            admission.MapId.Value);
        WriteGuid(
            frame,
            WorldInstanceIdOffset,
            admission.WorldInstanceId.Value);
        BinaryPrimitives.WriteInt64BigEndian(
            frame[IssuedAtOffset..],
            admission.IssuedAtUtc.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64BigEndian(
            frame[ExpiresAtOffset..],
            admission.ExpiresAtUtc.ToUnixTimeMilliseconds());
        WriteSource(frame, admission.ObservedClientSource);
        WriteAsciiSlot(
            frame,
            UsernameLengthOffset,
            UsernameOffset,
            BackhaulProtocolConstants.UsernameBytes,
            admission.Username);
        WriteAsciiSlot(
            frame,
            NodeLengthOffset,
            NodeOffset,
            BackhaulProtocolConstants.ServerNodeIdBytes,
            admission.TargetNodeId.ToString());
        written = frame.Length;
        return true;
    }

    public static bool TryDecodeOpenSession(
        ReadOnlySpan<byte> source,
        out GatewayWorldAdmission? admission,
        out BackhaulDecodeFailure failure)
    {
        admission = null;
        if (!TryValidateHeader(
                source,
                BackhaulProtocolConstants.OpenSessionFrameBytes,
                BackhaulMessageType.OpenSession,
                BackhaulProtocolConstants.OpenSessionPayloadBytes,
                out failure))
        {
            return false;
        }

        if (!TryReadGuid(
                source,
                GatewayBootIdOffset,
                out var gatewayBootId) ||
            !TryReadGuid(
                source,
                ConnectionIdOffset,
                out var connectionId) ||
            !TryReadGuid(
                source,
                LoginGenerationIdOffset,
                out var loginGenerationId) ||
            !TryReadGuid(
                source,
                WorldInstanceIdOffset,
                out var worldInstanceId) ||
            !TryReadSource(source, out var clientSource) ||
            !TryReadAsciiSlot(
                source,
                UsernameLengthOffset,
                UsernameOffset,
                BackhaulProtocolConstants.UsernameBytes,
                out var username) ||
            !TryReadAsciiSlot(
                source,
                NodeLengthOffset,
                NodeOffset,
                BackhaulProtocolConstants.ServerNodeIdBytes,
                out var nodeId))
        {
            failure = BackhaulDecodeFailure.InvalidAdmission;
            return false;
        }

        try
        {
            var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                BinaryPrimitives.ReadInt64BigEndian(
                    source[IssuedAtOffset..]));
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(
                BinaryPrimitives.ReadInt64BigEndian(
                    source[ExpiresAtOffset..]));
            admission = new GatewayWorldAdmission(
                gatewayBootId,
                connectionId,
                loginGenerationId,
                BinaryPrimitives.ReadInt32BigEndian(
                    source[AccountIdOffset..]),
                BinaryPrimitives.ReadInt32BigEndian(
                    source[CharacterIdOffset..]),
                username,
                new RealmId(
                    BinaryPrimitives.ReadInt32BigEndian(
                        source[RealmIdOffset..])),
                new MapId(
                    BinaryPrimitives.ReadInt16BigEndian(
                        source[MapIdOffset..])),
                new WorldInstanceId(worldInstanceId),
                new ServerNodeId(nodeId),
                issuedAt,
                expiresAt,
                clientSource);
            failure = BackhaulDecodeFailure.None;
            return true;
        }
        catch (Exception error)
            when (error is ArgumentException or
                ArgumentOutOfRangeException)
        {
            admission = null;
            failure = BackhaulDecodeFailure.InvalidAdmission;
            return false;
        }
    }

    public static bool TryEncodeAdmissionResponse(
        BackhaulAdmissionResponse response,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (destination.Length <
            BackhaulProtocolConstants.AdmissionResponseFrameBytes)
        {
            return false;
        }

        var frame = destination[
            ..BackhaulProtocolConstants.AdmissionResponseFrameBytes];
        frame.Clear();
        WriteHeader(
            frame,
            BackhaulMessageType.AdmissionResponse,
            BackhaulProtocolConstants.AdmissionResponsePayloadBytes);
        BinaryPrimitives.WriteUInt16BigEndian(
            frame[ResponseStatusOffset..],
            (ushort)response.Status);
        WriteGuidAllowEmpty(
            frame,
            ResponseConnectionIdOffset,
            response.ConnectionId);
        written = frame.Length;
        return true;
    }

    public static bool TryDecodeAdmissionResponse(
        ReadOnlySpan<byte> source,
        out BackhaulAdmissionResponse response,
        out BackhaulDecodeFailure failure)
    {
        response = default;
        if (!TryValidateHeader(
                source,
                BackhaulProtocolConstants.AdmissionResponseFrameBytes,
                BackhaulMessageType.AdmissionResponse,
                BackhaulProtocolConstants.AdmissionResponsePayloadBytes,
                out failure))
        {
            return false;
        }

        if (source.Slice(ResponseReservedOffset, 2)
            .IndexOfAnyExcept((byte)0) >= 0)
        {
            failure = BackhaulDecodeFailure.InvalidReservedBytes;
            return false;
        }

        var rawStatus = BinaryPrimitives.ReadUInt16BigEndian(
            source[ResponseStatusOffset..]);
        if (!Enum.IsDefined(
                typeof(BackhaulAdmissionStatus),
                rawStatus))
        {
            failure = BackhaulDecodeFailure.UnknownStatus;
            return false;
        }

        var connectionId = ReadGuidAllowEmpty(
            source,
            ResponseConnectionIdOffset);
        try
        {
            response = new BackhaulAdmissionResponse(
                (BackhaulAdmissionStatus)rawStatus,
                connectionId);
            failure = BackhaulDecodeFailure.None;
            return true;
        }
        catch (ArgumentException)
        {
            failure = BackhaulDecodeFailure.InvalidAdmission;
            return false;
        }
    }

    public static bool TryReadDeclaredFrameLength(
        ReadOnlySpan<byte> header,
        out int frameLength)
    {
        frameLength = 0;
        if (header.Length != BackhaulProtocolConstants.HeaderBytes ||
            BinaryPrimitives.ReadUInt32BigEndian(header) !=
                BackhaulProtocolConstants.Magic ||
            BinaryPrimitives.ReadUInt16BigEndian(header[4..]) !=
                BackhaulProtocolConstants.Version)
        {
            return false;
        }

        var payloadLength =
            BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (payloadLength >
            BackhaulProtocolConstants.MaximumFrameBytes -
                BackhaulProtocolConstants.HeaderBytes)
        {
            return false;
        }

        frameLength = checked(
            BackhaulProtocolConstants.HeaderBytes +
            (int)payloadLength);
        return true;
    }

    private static void WriteHeader(
        Span<byte> destination,
        BackhaulMessageType type,
        int payloadLength)
    {
        BinaryPrimitives.WriteUInt32BigEndian(
            destination,
            BackhaulProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[4..],
            BackhaulProtocolConstants.Version);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[6..],
            (ushort)type);
        BinaryPrimitives.WriteUInt32BigEndian(
            destination[8..],
            checked((uint)payloadLength));
    }

    private static bool TryValidateHeader(
        ReadOnlySpan<byte> source,
        int expectedLength,
        BackhaulMessageType expectedType,
        int expectedPayloadLength,
        out BackhaulDecodeFailure failure)
    {
        if (source.Length != expectedLength)
        {
            failure = BackhaulDecodeFailure.InvalidLength;
            return false;
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(source) !=
            BackhaulProtocolConstants.Magic)
        {
            failure = BackhaulDecodeFailure.InvalidMagic;
            return false;
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(source[4..]) !=
            BackhaulProtocolConstants.Version)
        {
            failure = BackhaulDecodeFailure.UnsupportedVersion;
            return false;
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(source[6..]) !=
            (ushort)expectedType)
        {
            failure = BackhaulDecodeFailure.WrongMessageType;
            return false;
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(source[8..]) !=
            expectedPayloadLength)
        {
            failure = BackhaulDecodeFailure.InvalidPayloadLength;
            return false;
        }

        failure = BackhaulDecodeFailure.None;
        return true;
    }

    private static void WriteGuid(
        Span<byte> destination,
        int offset,
        Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Canonical backhaul GUID fields cannot be empty.");
        }
        WriteGuidAllowEmpty(destination, offset, value);
    }

    private static void WriteGuidAllowEmpty(
        Span<byte> destination,
        int offset,
        Guid value)
    {
        if (!value.TryWriteBytes(
                destination.Slice(offset, 16),
                bigEndian: true,
                out var written) ||
            written != 16)
        {
            throw new InvalidOperationException(
                "A canonical network-order GUID could not be encoded.");
        }
    }

    private static bool TryReadGuid(
        ReadOnlySpan<byte> source,
        int offset,
        out Guid value)
    {
        value = ReadGuidAllowEmpty(source, offset);
        return value != Guid.Empty;
    }

    private static Guid ReadGuidAllowEmpty(
        ReadOnlySpan<byte> source,
        int offset) =>
        new(source.Slice(offset, 16), bigEndian: true);

    private static void WriteSource(
        Span<byte> destination,
        IPEndPoint source)
    {
        var address = source.Address;
        var bytes = address.GetAddressBytes();
        destination[SourceFamilyOffset] =
            bytes.Length == 4 ? (byte)4 : (byte)6;
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[SourcePortOffset..],
            checked((ushort)source.Port));
        if (bytes.Length == 4)
        {
            bytes.CopyTo(
                destination.Slice(
                    SourceAddressOffset + 12,
                    4));
            return;
        }
        if (bytes.Length == 16)
        {
            bytes.CopyTo(
                destination.Slice(SourceAddressOffset, 16));
            return;
        }

        throw new ArgumentException(
            "Only IPv4 and IPv6 client sources are supported.",
            nameof(source));
    }

    private static bool TryReadSource(
        ReadOnlySpan<byte> source,
        out IPEndPoint endpoint)
    {
        endpoint = default!;
        var family = source[SourceFamilyOffset];
        var addressBytes =
            source.Slice(SourceAddressOffset, 16);
        IPAddress address;
        if (family == 4)
        {
            if (addressBytes[..12].IndexOfAnyExcept((byte)0) >= 0)
            {
                return false;
            }
            address = new IPAddress(addressBytes[12..]);
        }
        else if (family == 6)
        {
            address = new IPAddress(addressBytes);
        }
        else
        {
            return false;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(
            source[SourcePortOffset..]);
        if (port == 0)
        {
            return false;
        }

        endpoint = new IPEndPoint(address, port);
        return true;
    }

    private static void WriteAsciiSlot(
        Span<byte> destination,
        int lengthOffset,
        int valueOffset,
        int capacity,
        string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"ASCII slot value must contain 1..{capacity} characters.");
        }

        destination[lengthOffset] = checked((byte)value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is < '!' or > '~')
            {
                throw new ArgumentException(
                    "Backhaul text fields must contain printable ASCII.",
                    nameof(value));
            }

            destination[valueOffset + index] = (byte)character;
        }
    }

    private static bool TryReadAsciiSlot(
        ReadOnlySpan<byte> source,
        int lengthOffset,
        int valueOffset,
        int capacity,
        out string value)
    {
        value = string.Empty;
        var length = source[lengthOffset];
        if (length is 0 || length > capacity)
        {
            return false;
        }

        var slot = source.Slice(valueOffset, capacity);
        var text = slot[..length];
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is < (byte)'!' or > (byte)'~')
            {
                return false;
            }
        }
        if (slot[length..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(text);
        return true;
    }
}
