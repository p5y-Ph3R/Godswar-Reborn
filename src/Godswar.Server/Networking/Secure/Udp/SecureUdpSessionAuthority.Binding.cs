using System.Net;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed partial class SecureUdpSessionAuthority
{
    public SecureUdpSessionBindStatus TryBind(
        ReadOnlySpan<byte> connectionIdBytes,
        ReadOnlySpan<byte> serverChallenge,
        ReadOnlySpan<byte> tlsProofAuthenticator,
        IPEndPoint remoteEndpoint,
        out SecureBoundGamePrincipal? principal)
    {
        return TryBind(
            connectionIdBytes,
            serverChallenge,
            tlsProofAuthenticator,
            remoteEndpoint,
            out principal,
            out _);
    }

    public SecureUdpSessionBindStatus TryBind(
        ReadOnlySpan<byte> connectionIdBytes,
        ReadOnlySpan<byte> serverChallenge,
        ReadOnlySpan<byte> tlsProofAuthenticator,
        IPEndPoint remoteEndpoint,
        out SecureBoundGamePrincipal? principal,
        out ulong bindingRevision)
    {
        principal = null;
        bindingRevision = 0;
        if (!SecureUdpConnectionKey.TryCreate(
                connectionIdBytes,
                out var connectionId) ||
            !SecureUdpBindingCodec.TryDecode(
                serverChallenge,
                out var challenge) ||
            challenge.Type != SecureUdpBindingType.ServerChallenge ||
            !CryptographicOperations.FixedTimeEquals(
                challenge.ConnectionId,
                connectionIdBytes) ||
            tlsProofAuthenticator.Length !=
                SecureUdpBindingConstants.TlsProofTagBytes)
        {
            return SecureUdpSessionBindStatus.InvalidProof;
        }
        if (!SecureUdpEndpointKey.TryCreate(
                remoteEndpoint,
                out var endpoint))
        {
            return SecureUdpSessionBindStatus.InvalidEndpoint;
        }

        Span<byte> proofKey = stackalloc byte[
            SecureUdpTlsProofAuthenticator.KeyBytes];
        Span<byte> fingerprintBytes = stackalloc byte[
            SecureUdpProofFingerprint.Bytes];
        long generation;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var lookupStatus = TryCopyBindingKey(
                        connectionId,
                        proofKey,
                        out generation);
                if (lookupStatus != SecureUdpSessionBindStatus.Bound)
                {
                    return lookupStatus;
                }
            }

            if (!SecureUdpTlsProofAuthenticator.Validate(
                    proofKey,
                    serverChallenge,
                    tlsProofAuthenticator) ||
                !SecureUdpProofFingerprint.TryCompute(
                    serverChallenge,
                    tlsProofAuthenticator,
                    fingerprintBytes,
                    out var fingerprint))
            {
                return SecureUdpSessionBindStatus.InvalidProof;
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_sessions.TryGetValue(
                        connectionId,
                        out var entry) ||
                    entry.Generation != generation)
                {
                    return SecureUdpSessionBindStatus.UnknownSession;
                }

                var now = _timeProvider.GetTimestamp();
                if (IsExpiredPending(entry, now) ||
                    IsExpiredBound(entry, now))
                {
                    RemoveAndClear(connectionId, entry);
                    return SecureUdpSessionBindStatus.Expired;
                }

                principal = entry.Principal;
                if (principal is null)
                {
                    return SecureUdpSessionBindStatus.UnknownSession;
                }

                var status = BindValidatedEndpoint(
                    entry,
                    endpoint,
                    fingerprint,
                    challenge.IssuedAtUnixSeconds,
                    now);
                bindingRevision = entry.BindingRevision;
                if (status is
                    SecureUdpSessionBindStatus.ReplayRejected or
                    SecureUdpSessionBindStatus.RebindRateLimited)
                {
                    principal = null;
                }
                return status;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proofKey);
            CryptographicOperations.ZeroMemory(fingerprintBytes);
        }
    }

    public bool IsBoundEndpoint(
        ReadOnlySpan<byte> connectionIdBytes,
        IPEndPoint remoteEndpoint)
    {
        if (!SecureUdpConnectionKey.TryCreate(
                connectionIdBytes,
                out var connectionId) ||
            !SecureUdpEndpointKey.TryCreate(
                remoteEndpoint,
                out var endpoint))
        {
            return false;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetTimestamp();
            if (!_sessions.TryGetValue(connectionId, out var entry))
            {
                return false;
            }
            if (IsExpiredPending(entry, now) ||
                IsExpiredBound(entry, now))
            {
                RemoveAndClear(connectionId, entry);
                return false;
            }
            return entry.BoundEndpoint == endpoint;
        }
    }

    public bool RecordAuthenticatedActivity(
        ReadOnlySpan<byte> connectionIdBytes,
        IPEndPoint remoteEndpoint)
    {
        if (!SecureUdpConnectionKey.TryCreate(
                connectionIdBytes,
                out var connectionId) ||
            !SecureUdpEndpointKey.TryCreate(
                remoteEndpoint,
                out var endpoint))
        {
            return false;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetTimestamp();
            if (!_sessions.TryGetValue(connectionId, out var entry) ||
                entry.BoundEndpoint != endpoint)
            {
                return false;
            }
            if (IsExpiredBound(entry, now))
            {
                RemoveAndClear(connectionId, entry);
                return false;
            }

            entry.LastActivityTimestamp = now;
            return true;
        }
    }

    public int CleanupExpiredSessions()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return CleanupExpired(_timeProvider.GetTimestamp());
        }
    }

    private SecureUdpSessionBindStatus TryCopyBindingKey(
        SecureUdpConnectionKey connectionId,
        Span<byte> proofKey,
        out long generation)
    {
        generation = 0;
        if (!_sessions.TryGetValue(connectionId, out var entry))
        {
            return SecureUdpSessionBindStatus.UnknownSession;
        }
        var now = _timeProvider.GetTimestamp();
        if (IsExpiredPending(entry, now) ||
            IsExpiredBound(entry, now))
        {
            RemoveAndClear(connectionId, entry);
            return SecureUdpSessionBindStatus.Expired;
        }

        entry.ProofKey.CopyTo(proofKey);
        generation = entry.Generation;
        return SecureUdpSessionBindStatus.Bound;
    }

    private SecureUdpSessionBindStatus BindValidatedEndpoint(
        SessionEntry entry,
        SecureUdpEndpointKey endpoint,
        SecureUdpProofFingerprint fingerprint,
        long proofIssuedAtUnixSeconds,
        long now)
    {
        if (entry.BoundEndpoint is null)
        {
            entry.BoundEndpoint = endpoint;
            entry.BindingRevision = 1;
            entry.RebindNotBeforeUnixSeconds =
                GetRebindNotBefore(proofIssuedAtUnixSeconds);
            entry.LastEndpointChangeTimestamp = now;
            entry.LastActivityTimestamp = now;
            RememberProof(entry, fingerprint);
            return SecureUdpSessionBindStatus.Bound;
        }

        if (entry.BoundEndpoint.Value == endpoint)
        {
            entry.LastActivityTimestamp = now;
            if (entry.CurrentProof == fingerprint)
            {
                return SecureUdpSessionBindStatus.AlreadyBound;
            }
            if (HasSeenProof(entry, fingerprint))
            {
                return SecureUdpSessionBindStatus.ReplayRejected;
            }

            return SecureUdpSessionBindStatus.AlreadyBound;
        }

        if (HasSeenProof(entry, fingerprint))
        {
            return SecureUdpSessionBindStatus.ReplayRejected;
        }
        if (now < entry.LastEndpointChangeTimestamp ||
            _timeProvider.GetElapsedTime(
                entry.LastEndpointChangeTimestamp,
                now) < _minimumRebindInterval)
        {
            return SecureUdpSessionBindStatus.RebindRateLimited;
        }
        if (proofIssuedAtUnixSeconds <
                entry.RebindNotBeforeUnixSeconds)
        {
            return SecureUdpSessionBindStatus.ReplayRejected;
        }

        entry.BoundEndpoint = endpoint;
        entry.BindingRevision = checked(entry.BindingRevision + 1);
        entry.RebindNotBeforeUnixSeconds =
            GetRebindNotBefore(proofIssuedAtUnixSeconds);
        entry.LastEndpointChangeTimestamp = now;
        entry.LastActivityTimestamp = now;
        RememberProof(entry, fingerprint);
        return SecureUdpSessionBindStatus.Rebound;
    }

    private long GetRebindNotBefore(long proofIssuedAtUnixSeconds)
    {
        var intervalSeconds = checked(
            (_minimumRebindInterval.Ticks +
                TimeSpan.TicksPerSecond - 1) /
            TimeSpan.TicksPerSecond);
        return checked(proofIssuedAtUnixSeconds + intervalSeconds);
    }

    private static bool HasSeenProof(
        SessionEntry entry,
        SecureUdpProofFingerprint fingerprint)
    {
        for (var index = 0; index < entry.RecentProofCount; index++)
        {
            if (entry.RecentProofs[index] == fingerprint)
            {
                return true;
            }
        }
        return false;
    }

    private static void RememberProof(
        SessionEntry entry,
        SecureUdpProofFingerprint fingerprint)
    {
        entry.CurrentProof = fingerprint;
        AddProofHistory(entry, fingerprint);
    }

    private static void AddProofHistory(
        SessionEntry entry,
        SecureUdpProofFingerprint fingerprint)
    {
        if (HasSeenProof(entry, fingerprint))
        {
            return;
        }
        if (entry.RecentProofCount <
            SecureUdpProofFingerprint.HistoryCapacity)
        {
            entry.RecentProofs[entry.RecentProofCount++] = fingerprint;
            return;
        }

        entry.RecentProofs[entry.RecentProofCursor] = fingerprint;
        entry.RecentProofCursor =
            (entry.RecentProofCursor + 1) %
            SecureUdpProofFingerprint.HistoryCapacity;
    }

}

internal readonly record struct SecureUdpProofFingerprint(
    ulong High,
    ulong Low)
{
    public const int Bytes = 16;
    public const int HistoryCapacity = 16;

    public static bool TryCompute(
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> tlsProofAuthenticator,
        Span<byte> destination,
        out SecureUdpProofFingerprint fingerprint)
    {
        fingerprint = default;
        if (challenge.Length != SecureUdpBindingConstants.DatagramBytes ||
            tlsProofAuthenticator.Length !=
                SecureUdpBindingConstants.TlsProofTagBytes ||
            destination.Length < Bytes)
        {
            return false;
        }

        Span<byte> input = stackalloc byte[
            SecureUdpBindingConstants.DatagramBytes +
            SecureUdpBindingConstants.TlsProofTagBytes];
        Span<byte> hash = stackalloc byte[32];
        try
        {
            challenge.CopyTo(input);
            tlsProofAuthenticator.CopyTo(
                input[SecureUdpBindingConstants.DatagramBytes..]);
            _ = SHA256.HashData(input, hash);
            hash[..Bytes].CopyTo(destination);
            fingerprint = new SecureUdpProofFingerprint(
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt64BigEndian(hash),
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt64BigEndian(hash[8..]));
            return fingerprint != default;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(hash);
        }
    }
}
