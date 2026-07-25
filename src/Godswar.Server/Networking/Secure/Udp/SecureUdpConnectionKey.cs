using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure.Udp;

internal readonly record struct SecureUdpConnectionKey(
    ulong High,
    ulong Low)
{
    public static bool TryCreate(
        ReadOnlySpan<byte> connectionId,
        out SecureUdpConnectionKey key)
    {
        key = default;
        if (connectionId.Length !=
                SecureUdpBindingConstants.ConnectionIdBytes ||
            SecureUdpBindingCodec.IsAllZero(connectionId))
        {
            return false;
        }

        key = new SecureUdpConnectionKey(
            BinaryPrimitives.ReadUInt64BigEndian(connectionId),
            BinaryPrimitives.ReadUInt64BigEndian(connectionId[8..]));
        return true;
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length <
            SecureUdpBindingConstants.ConnectionIdBytes)
        {
            throw new ArgumentException(
                "Connection-ID destination is too small.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, High);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], Low);
    }
}
