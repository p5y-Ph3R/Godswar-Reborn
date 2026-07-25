using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly int _authenticatedSessionCapacity;
    private readonly int _authenticatedSessionLimit;
    private readonly Dictionary<SecureUdpConnectionKey, int>
        _authenticatedSessionCounts = [];
    private readonly int _bindingProofLimit;
    private readonly int _bindingProofPrefixLimit;
    private readonly Dictionary<SecureUdpPrefix, int>
        _bindingProofPrefixCounts = [];
    private readonly int _protectedCandidateLimit;
    private readonly int _protectedCandidatePrefixLimit;
    private readonly Dictionary<SecureUdpPrefix, int>
        _protectedCandidatePrefixCounts = [];
    private readonly int _globalLimit;
    private readonly int _unvalidatedLimit;
    private readonly int _prefixCapacity;
    private readonly int _prefixLimit;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<SecureUdpPrefix, int> _prefixCounts = [];
    private int _globalCount;
    private int _bindingProofCount;
    private int _protectedCandidateCount;
    private int _unvalidatedCount;
    private long _windowTimestamp;

    public SecureUdpRateLimiter(
        int globalLimit,
        int prefixLimit,
        int prefixCapacity,
        TimeProvider? timeProvider = null)
        : this(
            globalLimit,
            unvalidatedLimit: globalLimit,
            prefixLimit,
            prefixCapacity,
            bindingProofLimit: globalLimit,
            bindingProofPrefixLimit: globalLimit,
            protectedCandidateLimit: globalLimit,
            protectedCandidatePrefixLimit: globalLimit,
            authenticatedSessionLimit: globalLimit,
            authenticatedSessionCapacity: 1,
            timeProvider)
    {
    }

    public SecureUdpRateLimiter(
        int globalLimit,
        int unvalidatedLimit,
        int prefixLimit,
        int prefixCapacity,
        int bindingProofLimit,
        int bindingProofPrefixLimit,
        int protectedCandidateLimit,
        int protectedCandidatePrefixLimit,
        int authenticatedSessionLimit,
        int authenticatedSessionCapacity,
        TimeProvider? timeProvider = null)
    {
        if (globalLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(globalLimit));
        }
        if (unvalidatedLimit < 1 || unvalidatedLimit > globalLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unvalidatedLimit));
        }
        if (prefixLimit < 1 || prefixLimit > unvalidatedLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLimit));
        }
        if (prefixCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixCapacity));
        }
        if (bindingProofLimit < 1 ||
            bindingProofLimit > globalLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindingProofLimit));
        }
        if (bindingProofPrefixLimit < 1 ||
            bindingProofPrefixLimit > bindingProofLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindingProofPrefixLimit));
        }
        if (protectedCandidateLimit < 1 ||
            protectedCandidateLimit > globalLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(protectedCandidateLimit));
        }
        if (protectedCandidatePrefixLimit < 1 ||
            protectedCandidatePrefixLimit > protectedCandidateLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(protectedCandidatePrefixLimit));
        }
        if (authenticatedSessionLimit < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authenticatedSessionLimit));
        }
        if (authenticatedSessionCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authenticatedSessionCapacity));
        }

        _globalLimit = globalLimit;
        _unvalidatedLimit = unvalidatedLimit;
        _prefixLimit = prefixLimit;
        _prefixCapacity = prefixCapacity;
        _bindingProofLimit = bindingProofLimit;
        _bindingProofPrefixLimit = bindingProofPrefixLimit;
        _protectedCandidateLimit = protectedCandidateLimit;
        _protectedCandidatePrefixLimit =
            protectedCandidatePrefixLimit;
        _authenticatedSessionLimit = authenticatedSessionLimit;
        _authenticatedSessionCapacity = authenticatedSessionCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowTimestamp = _timeProvider.GetTimestamp();
    }

    public bool TryAcquire(IPAddress address)
    {
        return TryAcquireUnvalidated(address);
    }

    public bool TryAcquirePending(IPAddress address)
    {
        return TryAcquireUnvalidated(address);
    }

    public bool TryAcquireUnvalidated(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!SecureUdpPrefix.TryCreate(address, out var prefix))
        {
            return false;
        }

        lock (_sync)
        {
            ResetWindowIfDue();
            if (_globalCount >= _globalLimit ||
                _unvalidatedCount >= _unvalidatedLimit)
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
            _unvalidatedCount++;
            _prefixCounts[prefix] = count + 1;
            return true;
        }
    }

    public bool TryAcquireBindingProof(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!SecureUdpPrefix.TryCreate(address, out var prefix))
        {
            return false;
        }

        lock (_sync)
        {
            ResetWindowIfDue();
            if (_globalCount >= _globalLimit ||
                _bindingProofCount >= _bindingProofLimit)
            {
                return false;
            }
            if (!_bindingProofPrefixCounts.TryGetValue(
                    prefix,
                    out var count))
            {
                if (_bindingProofPrefixCounts.Count >=
                    _prefixCapacity)
                {
                    return false;
                }
                count = 0;
            }
            if (count >= _bindingProofPrefixLimit)
            {
                return false;
            }

            _globalCount++;
            _bindingProofCount++;
            _bindingProofPrefixCounts[prefix] = count + 1;
            return true;
        }
    }

    public bool TryAcquireAuthenticatedSession(
        SecureUdpConnectionKey connectionId)
    {
        if (connectionId == default)
        {
            return false;
        }

        lock (_sync)
        {
            ResetWindowIfDue();
            if (!_authenticatedSessionCounts.TryGetValue(
                    connectionId,
                    out var count))
            {
                if (_authenticatedSessionCounts.Count >=
                    _authenticatedSessionCapacity)
                {
                    return false;
                }
                count = 0;
            }
            if (count >= _authenticatedSessionLimit)
            {
                return false;
            }

            _authenticatedSessionCounts[connectionId] = count + 1;
            return true;
        }
    }

    public bool TryAcquireProtectedCandidate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!SecureUdpPrefix.TryCreate(address, out var prefix))
        {
            return false;
        }

        lock (_sync)
        {
            ResetWindowIfDue();
            if (_globalCount >= _globalLimit ||
                _protectedCandidateCount >=
                    _protectedCandidateLimit)
            {
                return false;
            }
            if (!_protectedCandidatePrefixCounts.TryGetValue(
                    prefix,
                    out var count))
            {
                if (_protectedCandidatePrefixCounts.Count >=
                    _prefixCapacity)
                {
                    return false;
                }
                count = 0;
            }
            if (count >= _protectedCandidatePrefixLimit)
            {
                return false;
            }

            _globalCount++;
            _protectedCandidateCount++;
            _protectedCandidatePrefixCounts[prefix] = count + 1;
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
                _unvalidatedCount,
                _bindingProofCount,
                _protectedCandidateCount,
                _prefixCounts.Count,
                _bindingProofPrefixCounts.Count,
                _protectedCandidatePrefixCounts.Count,
                _authenticatedSessionCounts.Count,
                _globalLimit,
                _unvalidatedLimit,
                _bindingProofLimit,
                _protectedCandidateLimit,
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
        _unvalidatedCount = 0;
        _bindingProofCount = 0;
        _protectedCandidateCount = 0;
        _prefixCounts.Clear();
        _bindingProofPrefixCounts.Clear();
        _protectedCandidatePrefixCounts.Clear();
        _authenticatedSessionCounts.Clear();
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
    int UnvalidatedPackets,
    int BindingProofPackets,
    int ProtectedCandidatePackets,
    int ActivePrefixes,
    int ActiveBindingProofPrefixes,
    int ActiveProtectedCandidatePrefixes,
    int ActiveAuthenticatedSessions,
    int GlobalLimit,
    int UnvalidatedLimit,
    int BindingProofLimit,
    int ProtectedCandidateLimit,
    int PrefixCapacity);
