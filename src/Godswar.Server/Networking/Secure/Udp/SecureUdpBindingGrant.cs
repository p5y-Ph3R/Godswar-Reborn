using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpBindingGrant : IDisposable
{
    internal const uint Magic = 0x47575547; // GWUG
    internal const int ProofKeyBytes = 32;

    private readonly object _gate = new();
    private readonly byte[] _connectionId;
    private readonly byte[] _proofKey;
    private bool _disposed;

    public SecureUdpBindingGrant(
        ushort udpPort,
        uint serverId,
        ulong expiryUnixMilliseconds,
        ReadOnlySpan<byte> connectionId,
        ReadOnlySpan<byte> proofKey)
    {
        if (udpPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(udpPort));
        }
        if (serverId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serverId));
        }
        if (expiryUnixMilliseconds == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiryUnixMilliseconds));
        }
        if (connectionId.Length !=
                SecureUdpBindingConstants.ConnectionIdBytes ||
            SecureUdpBindingCodec.IsAllZero(connectionId))
        {
            throw new ArgumentException(
                "UDP grant connection ID must be exactly 16 nonzero bytes.",
                nameof(connectionId));
        }
        if (proofKey.Length != ProofKeyBytes ||
            SecureUdpBindingCodec.IsAllZero(proofKey))
        {
            throw new ArgumentException(
                "UDP grant proof key must be exactly 32 nonzero bytes.",
                nameof(proofKey));
        }

        UdpPort = udpPort;
        ServerId = serverId;
        ExpiryUnixMilliseconds = expiryUnixMilliseconds;
        _connectionId = connectionId.ToArray();
        _proofKey = proofKey.ToArray();
    }

    public ushort UdpPort { get; }

    public uint ServerId { get; }

    public ulong ExpiryUnixMilliseconds { get; }

    public bool TryCopySecrets(
        Span<byte> connectionIdDestination,
        Span<byte> proofKeyDestination)
    {
        if (connectionIdDestination.Length <
                SecureUdpBindingConstants.ConnectionIdBytes ||
            proofKeyDestination.Length < ProofKeyBytes ||
            connectionIdDestination.Overlaps(proofKeyDestination))
        {
            return false;
        }

        var connectionId = connectionIdDestination[
            ..SecureUdpBindingConstants.ConnectionIdBytes];
        var proofKey = proofKeyDestination[..ProofKeyBytes];
        lock (_gate)
        {
            if (_disposed)
            {
                connectionId.Clear();
                proofKey.Clear();
                return false;
            }

            _connectionId.CopyTo(connectionId);
            _proofKey.CopyTo(proofKey);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_connectionId);
            CryptographicOperations.ZeroMemory(_proofKey);
            _disposed = true;
        }
    }
}

internal static class SecureUdpBindingGrantCodec
{
    public static bool TryEncode(
        SecureUdpBindingGrant? grant,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (grant is null ||
            destination.Length <
                SecureProtocolConstants.UdpBindingGrantBytes)
        {
            return false;
        }

        var output = destination[
            ..SecureProtocolConstants.UdpBindingGrantBytes];
        output.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(
            output,
            SecureUdpBindingGrant.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[4..],
            SecureProtocolConstants.ProtocolMajor);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[6..],
            SecureProtocolConstants.ProtocolMinor);
        BinaryPrimitives.WriteUInt16BigEndian(
            output[8..],
            grant.UdpPort);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[12..],
            grant.ServerId);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[16..],
            grant.ExpiryUnixMilliseconds);
        if (!grant.TryCopySecrets(output[24..40], output[40..72]))
        {
            output.Clear();
            return false;
        }

        bytesWritten = output.Length;
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> source,
        out SecureUdpBindingGrant? grant)
    {
        grant = null;
        if (source.Length !=
                SecureProtocolConstants.UdpBindingGrantBytes ||
            BinaryPrimitives.ReadUInt32BigEndian(source) !=
                SecureUdpBindingGrant.Magic ||
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]) !=
                SecureProtocolConstants.ProtocolMajor ||
            BinaryPrimitives.ReadUInt16BigEndian(source[6..]) !=
                SecureProtocolConstants.ProtocolMinor ||
            source[10] != 0 ||
            source[11] != 0 ||
            SecureUdpBindingCodec.IsAllZero(source[24..40]) ||
            SecureUdpBindingCodec.IsAllZero(source[40..72]))
        {
            return false;
        }

        var udpPort = BinaryPrimitives.ReadUInt16BigEndian(source[8..]);
        var serverId = BinaryPrimitives.ReadUInt32BigEndian(source[12..]);
        var expiry = BinaryPrimitives.ReadUInt64BigEndian(source[16..]);
        if (udpPort == 0 || serverId == 0 || expiry == 0)
        {
            return false;
        }

        grant = new SecureUdpBindingGrant(
            udpPort,
            serverId,
            expiry,
            source[24..40],
            source[40..72]);
        return true;
    }
}
