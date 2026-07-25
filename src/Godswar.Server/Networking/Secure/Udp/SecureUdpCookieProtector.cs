using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpCookieProtector : IDisposable
{
    private static ReadOnlySpan<byte> Domain =>
        "GWSU-COOKIE-PROOF-V1"u8;

    private readonly SecureUdpCookiePolicy _policy;
    private readonly uint _serverId;
    private readonly ushort _udpPort;
    private readonly byte[] _audience;
    private readonly TimeProvider _timeProvider;
    private readonly SecureUdpCookieKeyRing _keyRing;
    private readonly long _originTimestamp;
    private readonly long _originUnixSeconds;
    private bool _disposed;

    public SecureUdpCookieProtector(
        SecureUdpCookiePolicy policy,
        uint serverId,
        ushort udpPort,
        string audience,
        TimeProvider? timeProvider = null)
        : this(
            policy,
            serverId,
            udpPort,
            audience,
            timeProvider ?? TimeProvider.System,
            null)
    {
    }

    internal SecureUdpCookieProtector(
        SecureUdpCookiePolicy policy,
        uint serverId,
        ushort udpPort,
        string audience,
        TimeProvider timeProvider,
        SecureUdpCookieKeyRing? keyRing)
    {
        policy.Validate();
        if (serverId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serverId));
        }
        if (udpPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(udpPort));
        }
        if (!SecureProtocolValidation.IsAudience(audience))
        {
            throw new ArgumentException(
                "UDP cookie audience must be a bounded protocol token.",
                nameof(audience));
        }

        _policy = policy;
        _serverId = serverId;
        _udpPort = udpPort;
        _audience = Encoding.ASCII.GetBytes(audience);
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        _keyRing = keyRing ??
            new SecureUdpCookieKeyRing(
                _timeProvider,
                policy.KeyRotation);
        _originTimestamp = _timeProvider.GetTimestamp();
        _originUnixSeconds =
            _timeProvider.GetUtcNow().ToUnixTimeSeconds();
    }

    public bool TryIssue(
        IPEndPoint remoteEndpoint,
        ReadOnlySpan<byte> connectionId,
        ReadOnlySpan<byte> clientNonce,
        out uint keyEpoch,
        out long issuedAtUnixSeconds,
        Span<byte> tagDestination)
    {
        ThrowIfDisposed();
        keyEpoch = 0;
        issuedAtUnixSeconds = 0;
        if (tagDestination.Length <
            SecureUdpBindingConstants.CookieTagBytes)
        {
            return false;
        }

        var now = GetLogicalUnixSeconds();
        if (now <= 0)
        {
            return false;
        }

        Span<byte> input = stackalloc byte[192];
        Span<byte> hash = stackalloc byte[
            SecureUdpCookieKeyRing.HashBytes];
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                keyEpoch = _keyRing.GetCurrentKeyId();
                issuedAtUnixSeconds = now;
                if (!TryWriteInput(
                        remoteEndpoint,
                        connectionId,
                        clientNonce,
                        keyEpoch,
                        issuedAtUnixSeconds,
                        input,
                        out var inputBytes) ||
                    !_keyRing.TryComputeHash(
                        keyEpoch,
                        input[..inputBytes],
                        hash))
                {
                    continue;
                }

                hash[..SecureUdpBindingConstants.CookieTagBytes].CopyTo(
                    tagDestination);
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(hash);
        }

        keyEpoch = 0;
        issuedAtUnixSeconds = 0;
        tagDestination[
            ..SecureUdpBindingConstants.CookieTagBytes].Clear();
        return false;
    }

    public bool Validate(
        IPEndPoint remoteEndpoint,
        ReadOnlySpan<byte> connectionId,
        ReadOnlySpan<byte> clientNonce,
        uint keyEpoch,
        long issuedAtUnixSeconds,
        ReadOnlySpan<byte> suppliedTag)
    {
        ThrowIfDisposed();
        if (keyEpoch == 0 ||
            issuedAtUnixSeconds <= 0 ||
            suppliedTag.Length !=
                SecureUdpBindingConstants.CookieTagBytes ||
            SecureUdpBindingCodec.IsAllZero(suppliedTag))
        {
            return false;
        }

        var now = GetLogicalUnixSeconds();
        var futureSeconds = issuedAtUnixSeconds > now
            ? issuedAtUnixSeconds - now
            : 0;
        var ageSeconds = now > issuedAtUnixSeconds
            ? now - issuedAtUnixSeconds
            : 0;
        if (futureSeconds > (long)_policy.FutureSkew.TotalSeconds ||
            ageSeconds > (long)_policy.Lifetime.TotalSeconds)
        {
            return false;
        }

        Span<byte> input = stackalloc byte[192];
        Span<byte> expected = stackalloc byte[
            SecureUdpCookieKeyRing.HashBytes];
        try
        {
            return TryWriteInput(
                    remoteEndpoint,
                    connectionId,
                    clientNonce,
                    keyEpoch,
                    issuedAtUnixSeconds,
                    input,
                    out var inputBytes) &&
                _keyRing.TryComputeHash(
                    keyEpoch,
                    input[..inputBytes],
                    expected) &&
                CryptographicOperations.FixedTimeEquals(
                    expected[
                        ..SecureUdpBindingConstants.CookieTagBytes],
                    suppliedTag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _keyRing.Dispose();
        CryptographicOperations.ZeroMemory(_audience);
        _disposed = true;
    }

    private bool TryWriteInput(
        IPEndPoint remoteEndpoint,
        ReadOnlySpan<byte> connectionId,
        ReadOnlySpan<byte> clientNonce,
        uint keyEpoch,
        long issuedAtUnixSeconds,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (remoteEndpoint is null ||
            remoteEndpoint.Port is < 1 or > ushort.MaxValue ||
            connectionId.Length !=
                SecureUdpBindingConstants.ConnectionIdBytes ||
            clientNonce.Length !=
                SecureUdpBindingConstants.ClientNonceBytes ||
            SecureUdpBindingCodec.IsAllZero(connectionId) ||
            SecureUdpBindingCodec.IsAllZero(clientNonce))
        {
            return false;
        }

        var address = remoteEndpoint.Address;
        var isMappedIpv4 = address.IsIPv4MappedToIPv6;
        var family = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => (byte)4,
            AddressFamily.InterNetworkV6 when isMappedIpv4 => (byte)4,
            AddressFamily.InterNetworkV6 => (byte)6,
            _ => (byte)0
        };
        if (family == 0)
        {
            return false;
        }

        destination.Clear();
        var offset = 0;
        Domain.CopyTo(destination);
        offset += Domain.Length;
        destination[offset++] =
            SecureUdpBindingConstants.ProtocolMajor;
        destination[offset++] =
            SecureUdpBindingConstants.ProtocolMinor;
        destination[offset++] = (byte)SecureUdpBindingType.ClientProof;
        BinaryPrimitives.WriteUInt32BigEndian(
            destination[offset..],
            keyEpoch);
        offset += sizeof(uint);
        BinaryPrimitives.WriteInt64BigEndian(
            destination[offset..],
            issuedAtUnixSeconds);
        offset += sizeof(long);
        BinaryPrimitives.WriteUInt32BigEndian(
            destination[offset..],
            _serverId);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            _udpPort);
        offset += sizeof(ushort);
        destination[offset++] = checked((byte)_audience.Length);
        _audience.CopyTo(destination[offset..]);
        offset += _audience.Length;
        destination[offset++] = family;
        var addressField = destination.Slice(offset, 16);
        Span<byte> rawAddress = stackalloc byte[16];
        rawAddress.Clear();
        if (!address.TryWriteBytes(
                rawAddress,
                out var rawAddressBytes))
        {
            return false;
        }
        var addressBytes = family == 4 ? 4 : 16;
        if (isMappedIpv4)
        {
            if (rawAddressBytes != 16)
            {
                return false;
            }
            rawAddress[12..].CopyTo(addressField);
        }
        else
        {
            if (rawAddressBytes != addressBytes)
            {
                return false;
            }
            rawAddress[..addressBytes].CopyTo(addressField);
        }
        var observedAddress = addressField[..addressBytes];
        if (SecureUdpBindingCodec.IsAllZero(observedAddress) ||
            family == 4 &&
                (observedAddress[0] is >= 224 and <= 239 ||
                    observedAddress[0] == 255 &&
                    observedAddress[1] == 255 &&
                    observedAddress[2] == 255 &&
                    observedAddress[3] == 255) ||
            family == 6 && observedAddress[0] == 0xFF)
        {
            return false;
        }
        offset += addressField.Length;
        BinaryPrimitives.WriteInt64BigEndian(
            destination[offset..],
            family == 6 ? address.ScopeId : 0);
        offset += sizeof(long);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            checked((ushort)remoteEndpoint.Port));
        offset += sizeof(ushort);
        connectionId.CopyTo(destination[offset..]);
        offset += connectionId.Length;
        clientNonce.CopyTo(destination[offset..]);
        offset += clientNonce.Length;
        bytesWritten = offset;
        return true;
    }

    private long GetLogicalUnixSeconds()
    {
        var timestamp = _timeProvider.GetTimestamp();
        if (timestamp <= _originTimestamp)
        {
            return _originUnixSeconds;
        }
        var elapsed = _timeProvider.GetElapsedTime(
            _originTimestamp,
            timestamp);
        return checked(
            _originUnixSeconds +
            elapsed.Ticks / TimeSpan.TicksPerSecond);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
