using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Networking.Backhaul;

internal readonly record struct BackhaulOwnedWorldRoute
{
    public BackhaulOwnedWorldRoute(
        RealmId realmId,
        MapId mapId,
        WorldInstanceId worldInstanceId)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentException(
                "A valid realm ID is required.",
                nameof(realmId));
        }
        if (!mapId.IsValid)
        {
            throw new ArgumentException(
                "A valid map ID is required.",
                nameof(mapId));
        }
        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentException(
                "A valid world-instance ID is required.",
                nameof(worldInstanceId));
        }

        RealmId = realmId;
        MapId = mapId;
        WorldInstanceId = worldInstanceId;
    }

    public RealmId RealmId { get; }

    public MapId MapId { get; }

    public WorldInstanceId WorldInstanceId { get; }

    public static BackhaulOwnedWorldRoute From(
        GatewayWorldAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return new BackhaulOwnedWorldRoute(
            admission.RealmId,
            admission.MapId,
            admission.WorldInstanceId);
    }
}

internal readonly record struct
    WorkerBackhaulAdmissionRegistrySnapshot(
        int Capacity,
        int ReplayCapacity,
        int TrackedAdmissions,
        int ReservedAdmissions,
        int ActiveAdmissions,
        int ReplayTombstones,
        int ActiveAccounts,
        int ReservedExpiryMarkers,
        int ReplayExpiryMarkers,
        long ReplayEvictions,
        bool IsDraining);

/// <summary>
/// Bounded worker-local admission and replay authority. Disposed sessions
/// move out of live capacity into a separately bounded tombstone index.
/// Retained IDs are replay-rejected; pressure evicts the earliest-expiring
/// tombstone deterministically.
/// </summary>
internal sealed class WorkerBackhaulAdmissionRegistry :
    IDisposable
{
    public const int MaximumCapacity = 100_000;
    public const int MaximumReplayCapacity = 1_000_000;
    public const int MaximumCleanupBatchSize = 1_024;
    public static readonly TimeSpan MaximumReplayRetention =
        TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumAdmissionLifetimeSafetyMargin =
        TimeSpan.FromMinutes(1);

    private readonly Dictionary<int, Guid> _accountAdmissions = [];
    private readonly int _capacity;
    private readonly int _cleanupBatchSize;
    private readonly SortedSet<ExpiryMarker> _liveExpiries =
        new(ExpiryMarkerComparer.Instance);
    private readonly Dictionary<Guid, AdmissionEntry> _live = [];
    private readonly int _replayCapacity;
    private readonly SortedSet<ExpiryMarker> _replayExpiries =
        new(ExpiryMarkerComparer.Instance);
    private readonly Dictionary<Guid, ExpiryMarker> _replays = [];
    private readonly object _gate = new();
    private readonly ServerNodeId _localNodeId;
    private readonly HashSet<BackhaulOwnedWorldRoute> _ownedRoutes;
    private readonly TimeSpan _admissionLifetimeSafetyMargin;
    private readonly TimeSpan _replayRetention;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;
    private bool _draining;
    private long _expirySequence;
    private long _replayEvictions;

    public WorkerBackhaulAdmissionRegistry(
        ServerNodeId localNodeId,
        IEnumerable<BackhaulOwnedWorldRoute> ownedRoutes,
        int capacity,
        int replayCapacity,
        TimeSpan replayRetention,
        TimeSpan admissionLifetimeSafetyMargin,
        int cleanupBatchSize = 64,
        TimeProvider? timeProvider = null)
    {
        if (!localNodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid local worker node ID is required.",
                nameof(localNodeId));
        }
        ArgumentNullException.ThrowIfNull(ownedRoutes);
        if (capacity is < 1 or > MaximumCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                $"Worker backhaul admission capacity must be between 1 " +
                $"and {MaximumCapacity}.");
        }
        if (replayCapacity is < 1 or > MaximumReplayCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replayCapacity),
                $"Worker replay capacity must be between 1 and " +
                $"{MaximumReplayCapacity}.");
        }
        if (replayRetention < TimeSpan.Zero ||
            replayRetention > MaximumReplayRetention)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replayRetention),
                "Replay retention must be between zero and ten minutes.");
        }
        if (admissionLifetimeSafetyMargin < TimeSpan.Zero ||
            admissionLifetimeSafetyMargin >
                MaximumAdmissionLifetimeSafetyMargin)
        {
            throw new ArgumentOutOfRangeException(
                nameof(admissionLifetimeSafetyMargin),
                "Admission lifetime safety margin must be between zero " +
                "and one minute.");
        }
        if (cleanupBatchSize is < 1 or > MaximumCleanupBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cleanupBatchSize),
                $"Cleanup batch size must be between 1 and " +
                $"{MaximumCleanupBatchSize}.");
        }

        var routes = ownedRoutes.ToHashSet();
        if (routes.Count == 0 ||
            routes.Count > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownedRoutes),
                "A worker must own between 1 and 65536 exact routes.");
        }

        _localNodeId = localNodeId;
        _ownedRoutes = routes;
        _capacity = capacity;
        _replayCapacity = replayCapacity;
        _cleanupBatchSize = cleanupBatchSize;
        _admissionLifetimeSafetyMargin =
            admissionLifetimeSafetyMargin;
        _replayRetention = replayRetention;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BackhaulAdmissionStatus TryReserve(
        GatewayWorldAdmission admission,
        out WorkerBackhaulAdmissionLease? lease)
    {
        ArgumentNullException.ThrowIfNull(admission);
        lease = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            CleanupExpired();
            if (_draining)
            {
                return BackhaulAdmissionStatus.Draining;
            }
            if (admission.TargetNodeId != _localNodeId ||
                !_ownedRoutes.Contains(
                    BackhaulOwnedWorldRoute.From(admission)))
            {
                return BackhaulAdmissionStatus.RouteRejected;
            }
            if (_live.ContainsKey(admission.ConnectionId) ||
                _replays.ContainsKey(admission.ConnectionId))
            {
                return BackhaulAdmissionStatus.ReplayRejected;
            }
            if (_accountAdmissions.ContainsKey(admission.AccountId))
            {
                return BackhaulAdmissionStatus.AccountAlreadyActive;
            }
            if (_live.Count >= _capacity)
            {
                return BackhaulAdmissionStatus.CapacityExceeded;
            }

            var expiry = NewExpiry(
                admission.ConnectionId,
                LocalReservationLifetime(admission));
            var entry = new AdmissionEntry(
                admission,
                AdmissionState.Reserved,
                expiry);
            _live.Add(admission.ConnectionId, entry);
            if (!_liveExpiries.Add(expiry))
            {
                _live.Remove(admission.ConnectionId);
                throw new InvalidOperationException(
                    "A unique live-admission expiry could not be tracked.");
            }
            _accountAdmissions.Add(
                admission.AccountId,
                admission.ConnectionId);
            lease = new WorkerBackhaulAdmissionLease(
                this,
                admission);
            return BackhaulAdmissionStatus.Accepted;
        }
    }

    public void BeginDrain()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _draining = true;
            }
        }
    }

    public WorkerBackhaulAdmissionRegistrySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            CleanupExpired();
            return new WorkerBackhaulAdmissionRegistrySnapshot(
                _capacity,
                _replayCapacity,
                _live.Count + _replays.Count,
                _live.Values.Count(static entry =>
                    entry.State == AdmissionState.Reserved),
                _live.Values.Count(static entry =>
                    entry.State == AdmissionState.Active),
                _replays.Count,
                _accountAdmissions.Count,
                _liveExpiries.Count,
                _replayExpiries.Count,
                _replayEvictions,
                _draining);
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

            _draining = true;
            _accountAdmissions.Clear();
            _live.Clear();
            _liveExpiries.Clear();
            _replays.Clear();
            _replayExpiries.Clear();
            _disposed = true;
        }
    }

    internal bool Activate(
        GatewayWorldAdmission admission)
    {
        lock (_gate)
        {
            if (_disposed ||
                !_live.TryGetValue(
                    admission.ConnectionId,
                    out var entry) ||
                entry.State != AdmissionState.Reserved ||
                !ReferenceEquals(entry.Admission, admission) ||
                !_accountAdmissions.TryGetValue(
                    admission.AccountId,
                    out var connectionId) ||
                connectionId != admission.ConnectionId)
            {
                return false;
            }

            entry.State = AdmissionState.Active;
            if (entry.Expiry is not { } expiry ||
                !_liveExpiries.Remove(expiry))
            {
                throw new InvalidOperationException(
                    "The reserved admission expiry is missing.");
            }
            entry.Expiry = null;
            return true;
        }
    }

    internal void Release(
        GatewayWorldAdmission admission)
    {
        lock (_gate)
        {
            if (_disposed ||
                !_live.TryGetValue(
                    admission.ConnectionId,
                    out var entry) ||
                !ReferenceEquals(entry.Admission, admission))
            {
                return;
            }

            _live.Remove(admission.ConnectionId);
            if (entry.Expiry is { } expiry &&
                !_liveExpiries.Remove(expiry))
            {
                throw new InvalidOperationException(
                    "The released admission expiry is missing.");
            }
            if (_accountAdmissions.TryGetValue(
                    admission.AccountId,
                    out var connectionId) &&
                connectionId == admission.ConnectionId)
            {
                _accountAdmissions.Remove(admission.AccountId);
            }

            AddReplay(
                admission.ConnectionId,
                _replayRetention);
        }
    }

    private void CleanupExpired()
    {
        var nowTimestamp = _timeProvider.GetTimestamp();
        for (var processed = 0;
             processed < _cleanupBatchSize;
             processed++)
        {
            var live = _liveExpiries.Count == 0
                ? (ExpiryMarker?)null
                : _liveExpiries.Min;
            var replay = _replayExpiries.Count == 0
                ? (ExpiryMarker?)null
                : _replayExpiries.Min;
            var liveDue = live is { } liveMarker &&
                liveMarker.DueTimestamp <= nowTimestamp;
            var replayDue = replay is { } replayMarker &&
                replayMarker.DueTimestamp <= nowTimestamp;
            if (!liveDue && !replayDue)
            {
                break;
            }

            if (liveDue &&
                (!replayDue ||
                 ExpiryMarkerComparer.Instance.Compare(
                     live!.Value,
                     replay!.Value) <= 0))
            {
                var marker = live!.Value;
                _liveExpiries.Remove(marker);
                if (!_live.TryGetValue(
                        marker.ConnectionId,
                        out var entry) ||
                    entry.State != AdmissionState.Reserved ||
                    entry.Expiry != marker)
                {
                    throw new InvalidOperationException(
                        "Live admission expiry accounting diverged.");
                }

                _live.Remove(marker.ConnectionId);
                entry.Expiry = null;
                if (_accountAdmissions.TryGetValue(
                        entry.Admission.AccountId,
                        out var connectionId) &&
                    connectionId == marker.ConnectionId)
                {
                    _accountAdmissions.Remove(
                        entry.Admission.AccountId);
                }

                AddReplay(
                    marker.ConnectionId,
                    _replayRetention);
                continue;
            }

            var expiredReplay = replay!.Value;
            _replayExpiries.Remove(expiredReplay);
            if (!_replays.TryGetValue(
                    expiredReplay.ConnectionId,
                    out var trackedReplay) ||
                trackedReplay != expiredReplay)
            {
                throw new InvalidOperationException(
                    "Replay expiry accounting diverged.");
            }

            _replays.Remove(expiredReplay.ConnectionId);
        }
    }

    private void AddReplay(
        Guid connectionId,
        TimeSpan retention)
    {
        while (_replays.Count >= _replayCapacity)
        {
            EvictEarliestReplay();
        }

        var marker = NewExpiry(connectionId, retention);
        if (!_replays.TryAdd(connectionId, marker) ||
            !_replayExpiries.Add(marker))
        {
            _replays.Remove(connectionId);
            throw new InvalidOperationException(
                "A unique replay tombstone could not be tracked.");
        }
    }

    private void EvictEarliestReplay()
    {
        if (_replayExpiries.Count == 0)
        {
            throw new InvalidOperationException(
                "Replay capacity is full without an eviction candidate.");
        }

        var marker = _replayExpiries.Min;
        _replayExpiries.Remove(marker);
        if (!_replays.Remove(marker.ConnectionId))
        {
            throw new InvalidOperationException(
                "Replay eviction accounting diverged.");
        }

        _replayEvictions++;
    }

    private ExpiryMarker NewExpiry(
        Guid connectionId,
        TimeSpan lifetime) =>
        new(
            connectionId,
            TimestampAfter(lifetime),
            checked(++_expirySequence));

    private long TimestampAfter(TimeSpan lifetime)
    {
        if (lifetime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var timestampDelta = checked((long)decimal.Ceiling(
            ((decimal)lifetime.Ticks /
                TimeSpan.TicksPerSecond) *
            _timeProvider.TimestampFrequency));
        return checked(
            _timeProvider.GetTimestamp() + timestampDelta);
    }

    private TimeSpan LocalReservationLifetime(
        GatewayWorldAdmission admission)
    {
        var declared =
            admission.ExpiresAtUtc - admission.IssuedAtUtc;
        var conservative =
            declared - _admissionLifetimeSafetyMargin;
        var halfLifetime =
            TimeSpan.FromTicks(declared.Ticks / 2);
        var floor = halfLifetime >= TimeSpan.FromMilliseconds(250)
            ? halfLifetime
            : TimeSpan.FromMilliseconds(250);
        var bounded = conservative >= floor
            ? conservative
            : floor;
        return bounded <= declared ? bounded : declared;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class AdmissionEntry(
        GatewayWorldAdmission admission,
        AdmissionState state,
        ExpiryMarker expiry)
    {
        public GatewayWorldAdmission Admission { get; } = admission;

        public AdmissionState State { get; set; } = state;

        public ExpiryMarker? Expiry { get; set; } = expiry;
    }

    private enum AdmissionState : byte
    {
        Reserved = 1,
        Active = 2
    }

    private readonly record struct ExpiryMarker(
        Guid ConnectionId,
        long DueTimestamp,
        long Sequence);

    private sealed class ExpiryMarkerComparer :
        IComparer<ExpiryMarker>
    {
        public static ExpiryMarkerComparer Instance { get; } = new();

        public int Compare(
            ExpiryMarker left,
            ExpiryMarker right)
        {
            var due = left.DueTimestamp.CompareTo(
                right.DueTimestamp);
            if (due != 0)
            {
                return due;
            }

            var sequence = left.Sequence.CompareTo(right.Sequence);
            return sequence != 0
                ? sequence
                : left.ConnectionId.CompareTo(right.ConnectionId);
        }
    }
}

internal sealed class WorkerBackhaulAdmissionLease :
    IDisposable
{
    private readonly WorkerBackhaulAdmissionRegistry _registry;
    private int _activated;
    private int _disposed;

    internal WorkerBackhaulAdmissionLease(
        WorkerBackhaulAdmissionRegistry registry,
        GatewayWorldAdmission admission)
    {
        _registry = registry;
        Admission = admission;
    }

    public GatewayWorldAdmission Admission { get; }

    public bool IsActive =>
        Volatile.Read(ref _activated) != 0 &&
        Volatile.Read(ref _disposed) == 0;

    public bool Activate()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        if (Volatile.Read(ref _activated) != 0)
        {
            return true;
        }
        if (!_registry.Activate(Admission))
        {
            return false;
        }

        Volatile.Write(ref _activated, 1);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _registry.Release(Admission);
        }
    }
}
