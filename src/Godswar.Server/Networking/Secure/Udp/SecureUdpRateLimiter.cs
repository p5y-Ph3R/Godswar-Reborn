using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly int _globalLimit;
    private readonly int _prefixCapacity;
    private readonly int _prefixLimit;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<SecureUdpPrefix, int> _prefixCounts = [];
    private int _globalCount;
    private long _windowTimestamp;

    public SecureUdpRateLimiter(
        int globalLimit,
        int prefixLimit,
        int prefixCapacity,
        TimeProvider? timeProvider = null)
    {
        if (globalLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(globalLimit));
        }
        if (prefixLimit < 1 || prefixLimit > globalLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLimit));
        }
        if (prefixCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixCapacity));
        }

        _globalLimit = globalLimit;
        _prefixLimit = prefixLimit;
        _prefixCapacity = prefixCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowTimestamp = _timeProvider.GetTimestamp();
    }

    public bool TryAcquire(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!SecureUdpPrefix.TryCreate(address, out var prefix))
        {
            return false;
        }

        lock (_sync)
        {
            ResetWindowIfDue();
            if (_globalCount >= _globalLimit)
            {
                return false;
            }
            if (!_prefixCounts.TryGetValue(prefix, out var count))
            {
                if (_prefixCounts.Count >= _prefixCapacity)
                {
                    return false;
                }
                count = 0;
            }
            if (count >= _prefixLimit)
            {
                return false;
            }

            _globalCount++;
            _prefixCounts[prefix] = count + 1;
            return true;
        }
    }

    internal SecureUdpRateLimiterSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            ResetWindowIfDue();
            return new SecureUdpRateLimiterSnapshot(
                _globalCount,
                _prefixCounts.Count,
                _globalLimit,
                _prefixCapacity);
        }
    }

    private void ResetWindowIfDue()
    {
        var now = _timeProvider.GetTimestamp();
        if (now < _windowTimestamp ||
            _timeProvider.GetElapsedTime(_windowTimestamp, now) < Window)
        {
            return;
        }

        _windowTimestamp = now;
        _globalCount = 0;
        _prefixCounts.Clear();
    }

    private readonly record struct SecureUdpPrefix(
        byte Family,
        ulong Value)
    {
        public static bool TryCreate(
            IPAddress address,
            out SecureUdpPrefix prefix)
        {
            prefix = default;
            var mapped = address.IsIPv4MappedToIPv6;
            var family = address.AddressFamily switch
            {
                AddressFamily.InterNetwork => (byte)4,
                AddressFamily.InterNetworkV6 when mapped => (byte)4,
                AddressFamily.InterNetworkV6 => (byte)6,
                _ => (byte)0
            };
            if (family == 0)
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[16];
            bytes.Clear();
            if (!address.TryWriteBytes(bytes, out var written))
            {
                return false;
            }
            if (family == 4)
            {
                var ipv4 = mapped ? bytes[12..16] : bytes[..4];
                if ((!mapped && written != 4) ||
                    (mapped && written != 16))
                {
                    return false;
                }
                var value =
                    BinaryPrimitives.ReadUInt32BigEndian(ipv4) &
                    0xFFFFFF00u;
                prefix = new SecureUdpPrefix(4, value);
                return true;
            }
            if (written != 16)
            {
                return false;
            }

            prefix = new SecureUdpPrefix(
                6,
                BinaryPrimitives.ReadUInt64BigEndian(bytes));
            return true;
        }
    }
}

internal readonly record struct SecureUdpRateLimiterSnapshot(
    int CurrentPackets,
    int ActivePrefixes,
    int GlobalLimit,
    int PrefixCapacity);
