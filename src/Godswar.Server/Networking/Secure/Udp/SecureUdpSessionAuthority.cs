using System.Net;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed partial class SecureUdpSessionAuthority : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<SecureUdpConnectionKey, SessionEntry>
        _sessions = [];
    private readonly int _capacity;
    private readonly TimeSpan _boundIdleTimeout;
    private readonly TimeSpan _minimumRebindInterval;
    private readonly TimeSpan _pendingTtl;
    private readonly TimeSpan _previousEpochOverlap;
    private readonly uint _serverId;
    private readonly TimeProvider _timeProvider;
    private readonly Func<byte[]> _proofKeyFactory;
    private readonly long _timeOriginTimestamp;
    private readonly long _timeOriginUnixMilliseconds;
    private long _nextGeneration;
    private bool _disposed;

    public SecureUdpSessionAuthority(
        int capacity,
        TimeSpan pendingTtl,
        TimeProvider? timeProvider = null)
        : this(
            capacity,
            pendingTtl,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2),
            1,
            TimeSpan.FromSeconds(10),
            timeProvider ?? TimeProvider.System,
            CreateProofKey)
    {
    }

    public SecureUdpSessionAuthority(
        int capacity,
        TimeSpan pendingTtl,
        TimeSpan boundIdleTimeout,
        TimeSpan minimumRebindInterval,
        TimeProvider? timeProvider = null)
        : this(
            capacity,
            pendingTtl,
            boundIdleTimeout,
            minimumRebindInterval,
            1,
            TimeSpan.FromSeconds(10),
            timeProvider ?? TimeProvider.System,
            CreateProofKey)
    {
    }

    public SecureUdpSessionAuthority(
        int capacity,
        TimeSpan pendingTtl,
        TimeSpan boundIdleTimeout,
        TimeSpan minimumRebindInterval,
        uint serverId,
        TimeSpan previousEpochOverlap,
        TimeProvider? timeProvider = null)
        : this(
            capacity,
            pendingTtl,
            boundIdleTimeout,
            minimumRebindInterval,
            serverId,
            previousEpochOverlap,
            timeProvider ?? TimeProvider.System,
            CreateProofKey)
    {
    }

    internal SecureUdpSessionAuthority(
        int capacity,
        TimeSpan pendingTtl,
        TimeProvider timeProvider,
        Func<byte[]> proofKeyFactory)
        : this(
            capacity,
            pendingTtl,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2),
            1,
            TimeSpan.FromSeconds(10),
            timeProvider,
            proofKeyFactory)
    {
    }

    internal SecureUdpSessionAuthority(
        int capacity,
        TimeSpan pendingTtl,
        TimeSpan boundIdleTimeout,
        TimeSpan minimumRebindInterval,
        TimeProvider timeProvider,
        Func<byte[]> proofKeyFactory)
        : this(
            capacity,
            pendingTtl,
            boundIdleTimeout,
            minimumRebindInterval,
            1,
            TimeSpan.FromSeconds(10),
            timeProvider,
            proofKeyFactory)
    {
    }

    internal SecureUdpSessionAuthority(
        int capacity,
        TimeSpan pendingTtl,
        TimeSpan boundIdleTimeout,
        TimeSpan minimumRebindInterval,
        uint serverId,
        TimeSpan previousEpochOverlap,
        TimeProvider timeProvider,
        Func<byte[]> proofKeyFactory)
    {
        if (capacity is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (pendingTtl < TimeSpan.FromSeconds(5) ||
            pendingTtl > TimeSpan.FromSeconds(120))
        {
            throw new ArgumentOutOfRangeException(nameof(pendingTtl));
        }
        if (boundIdleTimeout < TimeSpan.FromSeconds(15) ||
            boundIdleTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundIdleTimeout));
        }
        if (minimumRebindInterval < TimeSpan.FromMilliseconds(500) ||
            minimumRebindInterval > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumRebindInterval));
        }
        if (serverId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serverId));
        }
        if (previousEpochOverlap < TimeSpan.FromSeconds(1) ||
            previousEpochOverlap > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousEpochOverlap));
        }

        _capacity = capacity;
        _pendingTtl = pendingTtl;
        _boundIdleTimeout = boundIdleTimeout;
        _minimumRebindInterval = minimumRebindInterval;
        _serverId = serverId;
        _previousEpochOverlap = previousEpochOverlap;
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        _proofKeyFactory = proofKeyFactory ??
            throw new ArgumentNullException(nameof(proofKeyFactory));
        _timeOriginTimestamp = _timeProvider.GetTimestamp();
        _timeOriginUnixMilliseconds =
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    public SecureUdpSessionRegistrationResult Register(
        SecureConnectionContext connection,
        SecureBoundGamePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(principal);
        if (connection.Role != SecureEndpointRole.Game)
        {
            throw new ArgumentException(
                "Only an authenticated game TLS connection may register a UDP binding offer.",
                nameof(connection));
        }
        if (!SecureUdpConnectionKey.TryCreate(
                connection.ConnectionId.Span,
                out var connectionId))
        {
            throw new ArgumentException(
                "The TLS connection ID is not a canonical nonzero 16-byte value.",
                nameof(connection));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var nowTimestamp = _timeProvider.GetTimestamp();
            if (_sessions.TryGetValue(
                    connectionId,
                    out var existing))
            {
                if (IsExpiredPending(existing, nowTimestamp) ||
                    IsExpiredBound(existing, nowTimestamp))
                {
                    RemoveAndClear(connectionId, existing);
                }
                else
                {
                    return Rejected(
                        SecureUdpSessionRegistrationStatus
                            .DuplicateConnectionId);
                }
            }
            if (_sessions.Count >= _capacity)
            {
                CleanupExpired(nowTimestamp);
                if (_sessions.Count >= _capacity)
                {
                    return Rejected(
                        SecureUdpSessionRegistrationStatus
                            .CapacityExceeded);
                }
            }

            var proofKey = CreateAndValidateProofKey();
            var proofKeyOwned = true;
            try
            {
                var generation = checked(++_nextGeneration);
                if (generation <= 0)
                {
                    throw new InvalidOperationException(
                        "UDP session generation exhausted.");
                }

                Span<byte> protectedConnectionId = stackalloc byte[
                    SecureUdpProtectedConstants.ConnectionIdBytes];
                connectionId.WriteTo(protectedConnectionId);
                var protectedSession = new SecureUdpProtectedSession(
                    SecureUdpPeerRole.Server,
                    proofKey,
                    protectedConnectionId,
                    _serverId,
                    _previousEpochOverlap,
                    _timeProvider);
                var entry = new SessionEntry(
                    generation,
                    principal,
                    nowTimestamp,
                    GetPendingExpiryUnixMilliseconds(),
                    proofKey,
                    protectedSession);
                if (!_sessions.TryAdd(connectionId, entry))
                {
                    entry.Clear();
                    return Rejected(
                        SecureUdpSessionRegistrationStatus
                            .DuplicateConnectionId);
                }

                proofKeyOwned = false;
                return new SecureUdpSessionRegistrationResult(
                    SecureUdpSessionRegistrationStatus.Registered,
                    new SecureUdpSessionLease(
                        this,
                        connectionId,
                        generation));
            }
            finally
            {
                if (proofKeyOwned)
                {
                    CryptographicOperations.ZeroMemory(proofKey);
                }
            }
        }
    }

    public SecureUdpSessionAuthoritySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            CleanupExpired(_timeProvider.GetTimestamp());
            var bound = 0;
            foreach (var entry in _sessions.Values)
            {
                if (entry.BoundEndpoint is not null)
                {
                    bound++;
                }
            }

            return new SecureUdpSessionAuthoritySnapshot(
                _capacity,
                _sessions.Count - bound,
                bound);
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

            foreach (var entry in _sessions.Values)
            {
                entry.Clear();
            }
            _sessions.Clear();
            _disposed = true;
        }
    }

    internal bool TryCopyGrantMaterial(
        SecureUdpConnectionKey connectionId,
        long generation,
        Span<byte> connectionIdDestination,
        Span<byte> proofKeyDestination,
        out ulong expiryUnixMilliseconds)
    {
        expiryUnixMilliseconds = 0;
        if (connectionIdDestination.Length <
                SecureUdpBindingConstants.ConnectionIdBytes ||
            proofKeyDestination.Length <
                SecureUdpTlsProofAuthenticator.KeyBytes ||
            connectionIdDestination.Overlaps(proofKeyDestination))
        {
            return false;
        }

        var connectionIdOutput = connectionIdDestination[
            ..SecureUdpBindingConstants.ConnectionIdBytes];
        var proofKeyOutput = proofKeyDestination[
            ..SecureUdpTlsProofAuthenticator.KeyBytes];
        connectionIdOutput.Clear();
        proofKeyOutput.Clear();
        lock (_gate)
        {
            if (_disposed ||
                !_sessions.TryGetValue(connectionId, out var entry) ||
                entry.Generation != generation)
            {
                return false;
            }
            if (IsExpiredPending(
                    entry,
                    _timeProvider.GetTimestamp()))
            {
                RemoveAndClear(connectionId, entry);
                return false;
            }

            connectionId.WriteTo(connectionIdOutput);
            entry.ProofKey.CopyTo(proofKeyOutput);
            expiryUnixMilliseconds = entry.ExpiryUnixMilliseconds;
            return true;
        }
    }

    internal void Release(
        SecureUdpConnectionKey connectionId,
        long generation)
    {
        lock (_gate)
        {
            if (_disposed ||
                !_sessions.TryGetValue(connectionId, out var entry) ||
                entry.Generation != generation)
            {
                return;
            }

            RemoveAndClear(connectionId, entry);
        }
    }

    private int CleanupExpired(long nowTimestamp)
    {
        List<SecureUdpConnectionKey>? expired = null;
        foreach (var item in _sessions)
        {
            if (IsExpiredPending(item.Value, nowTimestamp) ||
                IsExpiredBound(item.Value, nowTimestamp))
            {
                expired ??= [];
                expired.Add(item.Key);
            }
        }
        if (expired is null)
        {
            return 0;
        }

        var removed = 0;
        foreach (var connectionId in expired)
        {
            if (_sessions.TryGetValue(connectionId, out var entry) &&
                (IsExpiredPending(entry, nowTimestamp) ||
                    IsExpiredBound(entry, nowTimestamp)))
            {
                RemoveAndClear(connectionId, entry);
                removed++;
            }
        }

        return removed;
    }

    private bool IsExpiredPending(
        SessionEntry entry,
        long nowTimestamp)
    {
        return entry.BoundEndpoint is null &&
            nowTimestamp >= entry.RegisteredTimestamp &&
            _timeProvider.GetElapsedTime(
                entry.RegisteredTimestamp,
                nowTimestamp) >= _pendingTtl;
    }

    private bool IsExpiredBound(
        SessionEntry entry,
        long nowTimestamp)
    {
        return entry.BoundEndpoint is not null &&
            nowTimestamp >= entry.LastActivityTimestamp &&
            _timeProvider.GetElapsedTime(
                entry.LastActivityTimestamp,
                nowTimestamp) >= _boundIdleTimeout;
    }

    private void RemoveAndClear(
        SecureUdpConnectionKey connectionId,
        SessionEntry entry)
    {
        if (_sessions.Remove(connectionId))
        {
            entry.Clear();
        }
    }

    private byte[] CreateAndValidateProofKey()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var proofKey = _proofKeyFactory();
            if (proofKey is not null &&
                proofKey.Length ==
                    SecureUdpTlsProofAuthenticator.KeyBytes &&
                !SecureUdpBindingCodec.IsAllZero(proofKey))
            {
                return proofKey;
            }
            if (proofKey is not null)
            {
                CryptographicOperations.ZeroMemory(proofKey);
            }
        }

        throw new CryptographicException(
            "UDP TLS-proof key factory repeatedly returned invalid key material.");
    }

    private ulong GetPendingExpiryUnixMilliseconds()
    {
        var nowTimestamp = _timeProvider.GetTimestamp();
        var logicalUnixMilliseconds = _timeOriginUnixMilliseconds;
        if (nowTimestamp > _timeOriginTimestamp)
        {
            logicalUnixMilliseconds = checked(
                logicalUnixMilliseconds +
                _timeProvider.GetElapsedTime(
                    _timeOriginTimestamp,
                    nowTimestamp).Ticks /
                TimeSpan.TicksPerMillisecond);
        }
        if (logicalUnixMilliseconds <= 0)
        {
            throw new InvalidOperationException(
                "UDP binding offer requires a positive logical Unix time.");
        }

        var ttlMilliseconds = checked(
            (_pendingTtl.Ticks +
                TimeSpan.TicksPerMillisecond - 1) /
            TimeSpan.TicksPerMillisecond);
        return checked(
            (ulong)logicalUnixMilliseconds +
            (ulong)ttlMilliseconds);
    }

    private static byte[] CreateProofKey()
    {
        return RandomNumberGenerator.GetBytes(
            SecureUdpTlsProofAuthenticator.KeyBytes);
    }

    private static SecureUdpSessionRegistrationResult Rejected(
        SecureUdpSessionRegistrationStatus status)
    {
        return new SecureUdpSessionRegistrationResult(status, null);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class SessionEntry(
        long generation,
        SecureBoundGamePrincipal principal,
        long registeredTimestamp,
        ulong expiryUnixMilliseconds,
        byte[] proofKey,
        SecureUdpProtectedSession protectedSession)
    {
        public long Generation { get; } = generation;

        public SecureBoundGamePrincipal? Principal { get; private set; } =
            principal;

        public long RegisteredTimestamp { get; } = registeredTimestamp;

        public ulong ExpiryUnixMilliseconds { get; } =
            expiryUnixMilliseconds;

        public byte[] ProofKey { get; } = proofKey;

        public SecureUdpProtectedSession ProtectedSession { get; } =
            protectedSession;

        public SecureUdpEndpointKey? BoundEndpoint { get; set; }

        public ulong BindingRevision { get; set; }

        public SecureUdpProofFingerprint CurrentProof { get; set; }

        public int RecentProofCount { get; set; }

        public int RecentProofCursor { get; set; }

        public SecureUdpProofFingerprint[] RecentProofs { get; } =
            new SecureUdpProofFingerprint[
                SecureUdpProofFingerprint.HistoryCapacity];

        public long LastActivityTimestamp { get; set; } =
            registeredTimestamp;

        public long LastEndpointChangeTimestamp { get; set; }

        public long RebindNotBeforeUnixSeconds { get; set; }

        public void Clear()
        {
            ProtectedSession.Dispose();
            CryptographicOperations.ZeroMemory(ProofKey);
            Array.Clear(RecentProofs);
            Principal = null;
            BoundEndpoint = null;
            BindingRevision = 0;
            CurrentProof = default;
            RecentProofCount = 0;
            RecentProofCursor = 0;
            LastActivityTimestamp = 0;
            LastEndpointChangeTimestamp = 0;
            RebindNotBeforeUnixSeconds = 0;
        }
    }
}
