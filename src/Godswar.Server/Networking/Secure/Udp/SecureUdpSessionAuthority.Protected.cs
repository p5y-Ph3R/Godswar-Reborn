using System.Net;

namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecureUdpProtectedAuthorityStatus : byte
{
    Accepted = 1,
    UnknownSession = 2,
    EndpointMismatch = 3,
    Expired = 4,
    ProtectedPacketRejected = 5,
    BindingRevisionMismatch = 6
}

internal readonly record struct SecureUdpProtectedAuthorityResult(
    SecureUdpProtectedAuthorityStatus Status,
    SecureBoundGamePrincipal? Principal,
    SecureUdpProtectedHeader Header,
    int PayloadBytes,
    ulong BindingRevision,
    SecureUdpProtectedError ProtectedError)
{
    public bool IsAccepted =>
        Status == SecureUdpProtectedAuthorityStatus.Accepted &&
        Principal is not null;
}

internal readonly record struct SecureUdpKeyRotationSweep(
    int NotDue,
    int Rotated,
    int EpochExhausted)
{
    public int SessionsChecked =>
        checked(NotDue + Rotated + EpochExhausted);
}

internal sealed partial class SecureUdpSessionAuthority
{
    public bool IsBoundEndpoint(
        SecureUdpConnectionKey connectionId,
        IPEndPoint remoteEndpoint)
    {
        if (connectionId == default ||
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

    public SecureUdpProtectedAuthorityResult TryUnprotect(
        SecureUdpConnectionKey connectionId,
        IPEndPoint remoteEndpoint,
        ReadOnlySpan<byte> datagram,
        Span<byte> plaintextDestination)
    {
        if (connectionId == default ||
            !SecureUdpEndpointKey.TryCreate(
                remoteEndpoint,
                out var endpoint))
        {
            return Rejected(
                SecureUdpProtectedAuthorityStatus.EndpointMismatch);
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sessions.TryGetValue(connectionId, out var entry))
            {
                return Rejected(
                    SecureUdpProtectedAuthorityStatus.UnknownSession);
            }

            var now = _timeProvider.GetTimestamp();
            if (IsExpiredPending(entry, now) ||
                IsExpiredBound(entry, now))
            {
                RemoveAndClear(connectionId, entry);
                return Rejected(
                    SecureUdpProtectedAuthorityStatus.Expired);
            }
            if (entry.BoundEndpoint != endpoint)
            {
                return Rejected(
                    SecureUdpProtectedAuthorityStatus.EndpointMismatch);
            }

            if (!entry.ProtectedSession.TryUnprotect(
                    datagram,
                    plaintextDestination,
                    out var header,
                    out var payloadBytes,
                    out var error))
            {
                return new SecureUdpProtectedAuthorityResult(
                    SecureUdpProtectedAuthorityStatus
                        .ProtectedPacketRejected,
                    null,
                    default,
                    0,
                    entry.BindingRevision,
                    error);
            }

            entry.LastActivityTimestamp = now;
            return new SecureUdpProtectedAuthorityResult(
                SecureUdpProtectedAuthorityStatus.Accepted,
                entry.Principal,
                header,
                payloadBytes,
                entry.BindingRevision,
                SecureUdpProtectedError.None);
        }
    }

    public bool TryProtect(
        SecureUdpConnectionKey connectionId,
        IPEndPoint remoteEndpoint,
        ulong expectedBindingRevision,
        SecureUdpProtectedMessageType messageType,
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        out int bytesWritten,
        out SecureUdpProtectedAuthorityStatus status,
        out SecureUdpProtectedError protectedError)
    {
        bytesWritten = 0;
        protectedError = SecureUdpProtectedError.InvalidArgument;
        if (connectionId == default ||
            expectedBindingRevision == 0 ||
            !SecureUdpEndpointKey.TryCreate(
                remoteEndpoint,
                out var endpoint))
        {
            status =
                SecureUdpProtectedAuthorityStatus.EndpointMismatch;
            return false;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sessions.TryGetValue(connectionId, out var entry))
            {
                status =
                    SecureUdpProtectedAuthorityStatus.UnknownSession;
                return false;
            }

            var now = _timeProvider.GetTimestamp();
            if (IsExpiredPending(entry, now) ||
                IsExpiredBound(entry, now))
            {
                RemoveAndClear(connectionId, entry);
                status = SecureUdpProtectedAuthorityStatus.Expired;
                return false;
            }
            if (entry.BoundEndpoint != endpoint)
            {
                status =
                    SecureUdpProtectedAuthorityStatus.EndpointMismatch;
                return false;
            }
            if (entry.BindingRevision != expectedBindingRevision)
            {
                status = SecureUdpProtectedAuthorityStatus
                    .BindingRevisionMismatch;
                return false;
            }

            if (!entry.ProtectedSession.TryProtect(
                    messageType,
                    payload,
                    destination,
                    out bytesWritten,
                    out protectedError))
            {
                status = SecureUdpProtectedAuthorityStatus
                    .ProtectedPacketRejected;
                return false;
            }

            status = SecureUdpProtectedAuthorityStatus.Accepted;
            return true;
        }
    }

    public SecureUdpKeyRotationSweep RotateProtectedSendKeys(
        ulong packetLimit,
        TimeSpan maximumAge)
    {
        var notDue = 0;
        var rotated = 0;
        var exhausted = 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var entry in _sessions.Values)
            {
                switch (entry.ProtectedSession.RotateSendEpochIfDue(
                    packetLimit,
                    maximumAge))
                {
                    case SecureUdpKeyRotationStatus.NotDue:
                        notDue++;
                        break;
                    case SecureUdpKeyRotationStatus.Rotated:
                        rotated++;
                        break;
                    case SecureUdpKeyRotationStatus.EpochExhausted:
                        exhausted++;
                        break;
                    case SecureUdpKeyRotationStatus.Disposed:
                        throw new InvalidOperationException(
                            "A tracked UDP session cannot own a disposed protected channel.");
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        return new SecureUdpKeyRotationSweep(
            notDue,
            rotated,
            exhausted);
    }

    private static SecureUdpProtectedAuthorityResult Rejected(
        SecureUdpProtectedAuthorityStatus status)
    {
        return new SecureUdpProtectedAuthorityResult(
            status,
            null,
            default,
            0,
            0,
            SecureUdpProtectedError.InvalidArgument);
    }
}
