using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Godswar.Server.Networking;

internal readonly record struct IpAddressKey(
    AddressFamily Family,
    ulong High,
    ulong Low)
{
    public static bool TryCreate(
        IPAddress? address,
        out IPAddress normalizedAddress,
        out IpAddressKey key)
    {
        normalizedAddress = IPAddress.None;
        key = default;

        if (address is null ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var bytesWritten))
        {
            return false;
        }

        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork when bytesWritten == 4:
                normalizedAddress = new IPAddress(bytes[..4]);
                key = new IpAddressKey(
                    AddressFamily.InterNetwork,
                    High: 0,
                    Low: BinaryPrimitives.ReadUInt32BigEndian(bytes));
                return true;

            case AddressFamily.InterNetworkV6 when bytesWritten == 16:
                normalizedAddress = new IPAddress(bytes);
                key = new IpAddressKey(
                    AddressFamily.InterNetworkV6,
                    BinaryPrimitives.ReadUInt64BigEndian(bytes),
                    BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
                return true;

            default:
                return false;
        }
    }
}

internal readonly record struct NetworkPrefixKey(
    AddressFamily Family,
    ulong Value)
{
    public static NetworkPrefixKey FromAddress(IpAddressKey address)
    {
        return address.Family switch
        {
            AddressFamily.InterNetwork => new NetworkPrefixKey(
                AddressFamily.InterNetwork,
                address.Low >> 8),
            AddressFamily.InterNetworkV6 => new NetworkPrefixKey(
                AddressFamily.InterNetworkV6,
                address.High),
            _ => throw new ArgumentOutOfRangeException(
                nameof(address),
                "Only IPv4 and IPv6 addresses have admission prefixes."),
        };
    }
}
