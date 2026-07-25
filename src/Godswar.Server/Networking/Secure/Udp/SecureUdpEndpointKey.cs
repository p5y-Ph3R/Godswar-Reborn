using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Godswar.Server.Networking.Secure.Udp;

internal readonly record struct SecureUdpEndpointKey(
    byte Family,
    ulong AddressHigh,
    ulong AddressLow,
    long ScopeId,
    ushort Port)
{
    public static bool TryCreate(
        IPEndPoint? endpoint,
        out SecureUdpEndpointKey key)
    {
        key = default;
        if (endpoint is null ||
            endpoint.Port is < 1 or > ushort.MaxValue)
        {
            return false;
        }

        var address = endpoint.Address;
        var mappedIpv4 = address.IsIPv4MappedToIPv6;
        var family = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => (byte)4,
            AddressFamily.InterNetworkV6 when mappedIpv4 => (byte)4,
            AddressFamily.InterNetworkV6 => (byte)6,
            _ => (byte)0
        };
        if (family == 0)
        {
            return false;
        }

        Span<byte> raw = stackalloc byte[16];
        raw.Clear();
        if (!address.TryWriteBytes(raw, out var rawBytes))
        {
            return false;
        }

        Span<byte> canonical = stackalloc byte[16];
        canonical.Clear();
        if (mappedIpv4)
        {
            if (rawBytes != 16)
            {
                return false;
            }
            raw[12..].CopyTo(canonical[12..]);
        }
        else if (family == 4)
        {
            if (rawBytes != 4)
            {
                return false;
            }
            raw[..4].CopyTo(canonical[12..]);
        }
        else
        {
            if (rawBytes != 16)
            {
                return false;
            }
            raw.CopyTo(canonical);
        }

        var observed = family == 4
            ? canonical[12..]
            : canonical;
        if (SecureUdpBindingCodec.IsAllZero(observed) ||
            family == 4 &&
                (observed[0] is >= 224 and <= 239 ||
                    observed[0] == 255 &&
                    observed[1] == 255 &&
                    observed[2] == 255 &&
                    observed[3] == 255) ||
            family == 6 && observed[0] == 0xFF)
        {
            return false;
        }

        key = new SecureUdpEndpointKey(
            family,
            BinaryPrimitives.ReadUInt64BigEndian(canonical),
            BinaryPrimitives.ReadUInt64BigEndian(canonical[8..]),
            family == 6 ? address.ScopeId : 0,
            checked((ushort)endpoint.Port));
        return true;
    }
}
