using System.Net;
using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecureRealtimeMovementOfferStatus : byte
{
    Accepted = 1,
    Replaced = 2,
    Duplicate = 3,
    StaleInput = 4,
    TransportEpochRejected = 5,
    TransportSourceRejected = 6,
    Malformed = 7,
    FeatureDisabled = 8,
    SessionUnavailable = 9,
    BindingRevisionMismatch = 10,
    MailboxUnavailable = 11,
    TransportChangedDuplicate = 12
}

internal readonly record struct SecureRealtimeMovementOfferResult(
    SecureRealtimeMovementOfferStatus Status,
    uint CurrentTransportEpoch,
    SecureRealtimeTransportSource CurrentTransportSource,
    ulong HighestInputId)
{
    public bool IsAccepted =>
        Status is SecureRealtimeMovementOfferStatus.Accepted or
            SecureRealtimeMovementOfferStatus.Replaced;

    public bool IsBenignProtocolResult =>
        Status is SecureRealtimeMovementOfferStatus.Accepted or
            SecureRealtimeMovementOfferStatus.Replaced or
            SecureRealtimeMovementOfferStatus.Duplicate or
            SecureRealtimeMovementOfferStatus
                .TransportChangedDuplicate or
            SecureRealtimeMovementOfferStatus.StaleInput or
            SecureRealtimeMovementOfferStatus
                .TransportEpochRejected or
            SecureRealtimeMovementOfferStatus
                .TransportSourceRejected;
}

internal readonly record struct SecureRealtimeSnapshotDispatch(
    SecureUdpConnectionKey ConnectionId,
    IPEndPoint RemoteEndpoint,
    ulong BindingRevision,
    SecureRealtimePositionSnapshot Snapshot);

internal enum SecureRealtimeSnapshotQueueStatus : byte
{
    NotNeeded = 1,
    Enqueued = 2,
    CapacityExceeded = 3
}

internal sealed partial class SecureUdpSessionAuthority
{
    private readonly bool _gameplayMovementEnabled;
    private readonly Queue<SecureUdpConnectionKey>
        _realtimeSnapshotReady = new();
    private readonly SemaphoreSlim _realtimeSnapshotAvailable = new(0);

    public bool GameplayMovementEnabled => _gameplayMovementEnabled;

    internal SecureUdpBindingCapabilities BindingCapabilities =>
        _gameplayMovementEnabled
            ? SecureUdpBindingCapabilities.AuthoritativeMovement
            : SecureUdpBindingCapabilities.None;

    internal int RealtimeSnapshotQueueCount
    {
        get
        {
            lock (_gate)
            {
                return _realtimeSnapshotReady.Count;
            }
        }
    }

    internal bool SupportsRealtimeMovement(
        SecureUdpConnectionKey connectionId,
        long generation)
    {
        if (!_gameplayMovementEnabled)
        {
            return false;
        }

        lock (_gate)
        {
            return !_disposed &&
                _sessions.TryGetValue(connectionId, out var entry) &&
                entry.Generation == generation &&
                entry.Realtime is not null;
        }
    }

    internal bool IsRealtimeMovementActive(
        SecureUdpConnectionKey connectionId,
        long generation)
    {
        if (!_gameplayMovementEnabled)
        {
            return false;
        }

        lock (_gate)
        {
            return !_disposed &&
                _sessions.TryGetValue(connectionId, out var entry) &&
                entry.Generation == generation &&
                entry.Realtime is not null &&
                entry.Realtime.Transport.GetSnapshot().HasTransport;
        }
    }

    internal SecureRealtimeMovementOfferResult OfferTlsMovement(
        SecureUdpConnectionKey connectionId,
        long generation,
        ReadOnlySpan<byte> payload)
    {
        if (!SecureRealtimeMovementProtocol.TryDecodeMovementInput(
                payload,
                SecureRealtimeTransportSource.Tls,
                out var input))
        {
            return Rejected(
                SecureRealtimeMovementOfferStatus.Malformed);
        }

        lock (_gate)
        {
            if (!_gameplayMovementEnabled)
            {
                return Rejected(
                    SecureRealtimeMovementOfferStatus.FeatureDisabled);
            }
            if (_disposed ||
                !_sessions.TryGetValue(connectionId, out var entry) ||
                entry.Generation != generation ||
                entry.Realtime is null)
            {
                return Rejected(
                    SecureRealtimeMovementOfferStatus
                        .SessionUnavailable);
            }
            var now = _timeProvider.GetTimestamp();
            if (IsExpiredPending(entry, now) ||
                IsExpiredBound(entry, now))
            {
                RemoveAndClear(connectionId, entry);
                return Rejected(
                    SecureRealtimeMovementOfferStatus
                        .SessionUnavailable);
            }
            entry.LastActivityTimestamp = now;

            return OfferMovementLocked(
                entry,
                SecureRealtimeTransportSource.Tls,
                input);
        }
    }

    internal SecureRealtimeMovementOfferResult OfferUdpMovement(
        SecureUdpConnectionKey connectionId,
        ulong expectedBindingRevision,
        in SecureRealtimeMovementInput input)
    {
        if (!SecureRealtimeMovementProtocol.IsValid(
                input,
                SecureRealtimeTransportSource.Udp))
        {
            return Rejected(
                SecureRealtimeMovementOfferStatus.Malformed);
        }

        lock (_gate)
        {
            if (!_gameplayMovementEnabled)
            {
                return Rejected(
                    SecureRealtimeMovementOfferStatus.FeatureDisabled);
            }
            if (_disposed ||
                !_sessions.TryGetValue(connectionId, out var entry) ||
                entry.Realtime is null)
            {
                return Rejected(
                    SecureRealtimeMovementOfferStatus
                        .SessionUnavailable);
            }
            if (entry.BindingRevision == 0 ||
                entry.BindingRevision != expectedBindingRevision)
            {
                return Rejected(
                    SecureRealtimeMovementOfferStatus
                        .BindingRevisionMismatch);
            }
            var now = _timeProvider.GetTimestamp();
            if (IsExpiredBound(entry, now))
            {
                RemoveAndClear(connectionId, entry);
                return Rejected(
                    SecureRealtimeMovementOfferStatus
                        .SessionUnavailable);
            }
            entry.LastActivityTimestamp = now;

            return OfferMovementLocked(
                entry,
                SecureRealtimeTransportSource.Udp,
                input);
        }
    }

    internal bool TryTakeRealtimeMovement(
        SecureUdpConnectionKey connectionId,
        long generation,
        out SecureRealtimeMovementIngress ingress)
    {
        lock (_gate)
        {
            if (!_gameplayMovementEnabled ||
                _disposed ||
                !_sessions.TryGetValue(connectionId, out var entry) ||
                entry.Generation != generation ||
                entry.Realtime is null)
            {
                ingress = default;
                return false;
            }

            return entry.Realtime.MovementIngress.TryTake(
                out ingress);
        }
    }

    internal bool TryPublishRealtimeSnapshot(
        SecureUdpConnectionKey connectionId,
        long generation,
        in SecureRealtimePositionSnapshot snapshot)
    {
        var signal = false;
        lock (_gate)
        {
            if (!_gameplayMovementEnabled ||
                _disposed ||
                !SecureRealtimeMovementProtocol.IsValid(snapshot) ||
                !_sessions.TryGetValue(connectionId, out var entry) ||
                entry.Generation != generation ||
                entry.Realtime is null)
            {
                return false;
            }

            var transport = entry.Realtime.Transport.GetSnapshot();
            var isInitialKeyframe =
                !transport.HasTransport &&
                snapshot.TransportEpoch == 1 &&
                snapshot.AcknowledgedInputId == 0 &&
                (snapshot.Flags &
                    SecureRealtimeSnapshotFlags.Keyframe) != 0 &&
                snapshot.Rejection ==
                    SecureRealtimeMovementRejection.None;
            if (!isInitialKeyframe &&
                (!transport.HasTransport ||
                    snapshot.TransportEpoch !=
                        transport.TransportEpoch))
            {
                return false;
            }

            var offered = entry.Realtime.SnapshotEgress.Offer(snapshot);
            if (offered == SecureRealtimeMailboxOfferStatus.Disposed)
            {
                return false;
            }
            var queueStatus = QueueRealtimeSnapshotLocked(
                connectionId,
                entry);
            if (queueStatus ==
                SecureRealtimeSnapshotQueueStatus.CapacityExceeded)
            {
                entry.Realtime.SnapshotEgress.TryTake(out _);
                return false;
            }
            signal =
                queueStatus ==
                    SecureRealtimeSnapshotQueueStatus.Enqueued;
        }

        if (signal)
        {
            _realtimeSnapshotAvailable.Release();
        }
        return true;
    }

    internal async ValueTask<SecureRealtimeSnapshotDispatch?>
        WaitForRealtimeSnapshotAsync(
            CancellationToken cancellationToken)
    {
        while (true)
        {
            await _realtimeSnapshotAvailable.WaitAsync(
                cancellationToken);
            lock (_gate)
            {
                if (_disposed)
                {
                    return null;
                }
                if (_realtimeSnapshotReady.Count == 0)
                {
                    continue;
                }

                var connectionId =
                    _realtimeSnapshotReady.Dequeue();
                if (!_sessions.TryGetValue(
                        connectionId,
                        out var entry) ||
                    !entry.RealtimeSnapshotQueued ||
                    entry.Realtime is null)
                {
                    continue;
                }

                entry.RealtimeSnapshotQueued = false;
                if (entry.BoundEndpoint is null ||
                    entry.BindingRevision == 0 ||
                    !entry.Realtime.SnapshotEgress.TryTake(
                        out var snapshot))
                {
                    continue;
                }

                return new SecureRealtimeSnapshotDispatch(
                    connectionId,
                    entry.BoundEndpoint.Value.ToIPEndPoint(),
                    entry.BindingRevision,
                    snapshot);
            }
        }
    }

    private SecureRealtimeMovementOfferResult
        OfferMovementLocked(
            SessionEntry entry,
            SecureRealtimeTransportSource source,
            in SecureRealtimeMovementInput input)
    {
        var realtime = entry.Realtime!;
        var currentTransport = realtime.Transport.GetSnapshot();
        if (!currentTransport.HasTransport &&
            source == SecureRealtimeTransportSource.Tls &&
            input.TransportEpoch == 2 &&
            (input.Flags &
                SecureRealtimeMovementFlags.CurrentWorld) == 0)
        {
            return Rejected(
                SecureRealtimeMovementOfferStatus
                    .TransportEpochRejected);
        }
        var reconciliation = realtime.Transport.Reconcile(
            source,
            input.TransportEpoch,
            input.InputId);
        if (reconciliation.Status ==
            SecureRealtimeReconciliationStatus
                .TransportChangedDuplicate)
        {
            return OfferTransportTransitionLocked(
                realtime,
                source,
                input,
                reconciliation);
        }
        if (!reconciliation.ShouldEnqueue)
        {
            return FromReconciliation(reconciliation);
        }

        var receivedTimestamp = _timeProvider.GetTimestamp();
        var receivedElapsed = receivedTimestamp >=
                _timeOriginTimestamp
            ? _timeProvider.GetElapsedTime(
                _timeOriginTimestamp,
                receivedTimestamp)
            : TimeSpan.Zero;
        var ingress = new SecureRealtimeMovementIngress(
            input,
            source,
            receivedElapsed,
            SecureRealtimeMovementIngressKind.Input);
        var mailbox = realtime.MovementIngress.Offer(ingress);
        return mailbox switch
        {
            SecureRealtimeMailboxOfferStatus.Accepted =>
                Result(
                    SecureRealtimeMovementOfferStatus.Accepted,
                    reconciliation),
            SecureRealtimeMailboxOfferStatus.Replaced =>
                Result(
                    SecureRealtimeMovementOfferStatus.Replaced,
                    reconciliation),
            SecureRealtimeMailboxOfferStatus.Disposed =>
                Result(
                    SecureRealtimeMovementOfferStatus
                        .MailboxUnavailable,
                    reconciliation),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mailbox))
        };
    }

    private static SecureRealtimeMovementOfferResult
        FromReconciliation(
            SecureRealtimeReconciliationResult reconciliation)
    {
        var status = reconciliation.Status switch
        {
            SecureRealtimeReconciliationStatus.Duplicate =>
                SecureRealtimeMovementOfferStatus.Duplicate,
            SecureRealtimeReconciliationStatus
                    .TransportChangedDuplicate =>
                SecureRealtimeMovementOfferStatus
                    .TransportChangedDuplicate,
            SecureRealtimeReconciliationStatus.StaleInput =>
                SecureRealtimeMovementOfferStatus.StaleInput,
            SecureRealtimeReconciliationStatus
                    .TransportEpochRejected =>
                SecureRealtimeMovementOfferStatus
                    .TransportEpochRejected,
            SecureRealtimeReconciliationStatus
                    .TransportSourceRejected =>
                SecureRealtimeMovementOfferStatus
                    .TransportSourceRejected,
            _ => throw new ArgumentOutOfRangeException(
                nameof(reconciliation))
        };
        return Result(status, reconciliation);
    }

    private SecureRealtimeMovementOfferResult
        OfferTransportTransitionLocked(
            SecureRealtimeSessionState realtime,
            SecureRealtimeTransportSource source,
            in SecureRealtimeMovementInput retry,
            SecureRealtimeReconciliationResult reconciliation)
    {
        var receivedTimestamp = _timeProvider.GetTimestamp();
        var receivedElapsed = receivedTimestamp >=
                _timeOriginTimestamp
            ? _timeProvider.GetElapsedTime(
                _timeOriginTimestamp,
                receivedTimestamp)
            : TimeSpan.Zero;
        var transitionedInput = retry;
        if (realtime.MovementIngress.TryTake(
                out var pending) &&
            pending.Input.InputId == retry.InputId)
        {
            transitionedInput = pending.Input with
            {
                Flags = retry.Flags,
                TransportEpoch = retry.TransportEpoch
            };
            receivedElapsed = pending.ServerReceiveElapsed;
        }

        var transition = new SecureRealtimeMovementIngress(
            transitionedInput,
            source,
            receivedElapsed,
            SecureRealtimeMovementIngressKind.TransportTransition);
        var mailbox = realtime.MovementIngress.Offer(transition);
        return mailbox == SecureRealtimeMailboxOfferStatus.Disposed
            ? Result(
                SecureRealtimeMovementOfferStatus.MailboxUnavailable,
                reconciliation)
            : Result(
                SecureRealtimeMovementOfferStatus
                    .TransportChangedDuplicate,
                reconciliation);
    }

    private static SecureRealtimeMovementOfferResult Result(
        SecureRealtimeMovementOfferStatus status,
        SecureRealtimeReconciliationResult reconciliation) =>
        new(
            status,
            reconciliation.CurrentTransportEpoch,
            reconciliation.CurrentTransportSource,
            reconciliation.HighestInputId);

    private static SecureRealtimeMovementOfferResult Rejected(
        SecureRealtimeMovementOfferStatus status) =>
        new(status, 0, default, 0);

    private SecureRealtimeSnapshotQueueStatus
        QueueRealtimeSnapshotLocked(
        SecureUdpConnectionKey connectionId,
        SessionEntry entry)
    {
        if (entry.BoundEndpoint is null ||
            entry.RealtimeSnapshotQueued ||
            entry.Realtime is null ||
            !entry.Realtime.SnapshotEgress.GetSnapshot().HasItem)
        {
            return SecureRealtimeSnapshotQueueStatus.NotNeeded;
        }
        if (_realtimeSnapshotReady.Count >= _capacity)
        {
            return SecureRealtimeSnapshotQueueStatus.CapacityExceeded;
        }

        entry.RealtimeSnapshotQueued = true;
        _realtimeSnapshotReady.Enqueue(connectionId);
        return SecureRealtimeSnapshotQueueStatus.Enqueued;
    }

    private sealed partial class SessionEntry
    {
        public SecureRealtimeSessionState? Realtime { get; private set; }

        public bool RealtimeSnapshotQueued { get; set; }

        public void EnableRealtime()
        {
            Realtime ??= new SecureRealtimeSessionState();
        }
    }
}
