using Godswar.Server.Application.Coordination;

namespace Godswar.Server.Infrastructure.Coordination;

internal sealed partial class InMemoryWorkerCoordination
{
    public ValueTask<PlayerLeaseResult> InstallPlayerLeaseAsync(
        PlayerLeaseInstallRequest request,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ValidateTtl(ttl);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (DeadlineExpired(deadline))
            {
                return ValueTask.FromResult(
                    new PlayerLeaseResult(Timeout(), null));
            }

            var now = _timeProvider.GetUtcNow();
            CleanupExpired(now);
            if (!WorkerOwnsRoute(
                    request.NodeId,
                    request.WorkerBootId,
                    request.Route))
            {
                return ValueTask.FromResult(
                    new PlayerLeaseResult(Conflict(), null));
            }

            if (_players.TryGetValue(
                    request.CharacterId,
                    out var current))
            {
                if (current.Lease.AccountId != request.AccountId)
                {
                    return ValueTask.FromResult(
                        new PlayerLeaseResult(Conflict(), null));
                }
                var same =
                    current.Lease.AccountId == request.AccountId &&
                    current.Lease.Ownership == request.Ownership &&
                    current.Lease.LeaseToken == request.LeaseToken &&
                    current.Lease.NodeId == request.NodeId &&
                    current.Lease.WorkerBootId == request.WorkerBootId;
                if (!same &&
                    request.Ownership.Generation <=
                        current.Lease.Ownership.Generation)
                {
                    return ValueTask.FromResult(
                        new PlayerLeaseResult(Conflict(), null));
                }
            }
            else if (_players.Count >= _capacity)
            {
                return ValueTask.FromResult(
                    new PlayerLeaseResult(Overloaded(), null));
            }

            if (!request.AllowAccountReplacement &&
                _playerByAccount.TryGetValue(
                    request.AccountId,
                    out var indexedCharacterId) &&
                indexedCharacterId != request.CharacterId)
            {
                return ValueTask.FromResult(
                    new PlayerLeaseResult(Conflict(), null));
            }

            RemoveAccountPlayer(request.AccountId, request.CharacterId);
            var version = current is null
                ? 1
                : checked(current.Lease.Version + 1);
            var lease = new CoordinatedPlayerLease(
                request.AccountId,
                request.CharacterId,
                request.Ownership,
                request.LeaseToken,
                request.NodeId,
                request.WorkerBootId,
                request.Route,
                request.Presence,
                version,
                now + ttl);
            _players[request.CharacterId] = new PlayerEntry(lease);
            _playerByAccount[request.AccountId] =
                request.CharacterId;
            return ValueTask.FromResult(
                new PlayerLeaseResult(
                    Accepted(CoordinationOperationStatus.Applied),
                    lease));
        }
    }

    public ValueTask<PlayerLeaseResult> RenewPlayerLeaseAsync(
        CoordinatedPlayerLease lease,
        CoordinatedWorldRoute route,
        CoordinatedPresenceState presence,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsValid)
        {
            throw new ArgumentException(
                "A valid player lease is required.",
                nameof(lease));
        }
        route.Validate();
        if (!Enum.IsDefined(presence))
        {
            throw new ArgumentOutOfRangeException(nameof(presence));
        }
        ValidateTtl(ttl);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (DeadlineExpired(deadline))
            {
                return ValueTask.FromResult(
                    new PlayerLeaseResult(Timeout(), null));
            }

            var now = _timeProvider.GetUtcNow();
            CleanupExpired(now);
            if (!_players.TryGetValue(
                    lease.CharacterId,
                    out var current))
            {
                return ValueTask.FromResult(
                    new PlayerLeaseResult(
                        CoordinationOperationStatus.NotFound,
                        null));
            }
            if (!SameOwner(current.Lease, lease) ||
                !WorkerOwnsRoute(
                    lease.NodeId,
                    lease.WorkerBootId,
                    route,
                    allowDraining:
                        presence ==
                        CoordinatedPresenceState.Draining))
            {
                return ValueTask.FromResult(
                    new PlayerLeaseResult(Conflict(), null));
            }

            var updated = current.Lease with
            {
                Route = route,
                Presence = presence,
                Version = checked(current.Lease.Version + 1),
                ProvenUntilUtc = now + ttl
            };
            current.Lease = updated;
            return ValueTask.FromResult(
                new PlayerLeaseResult(
                    Accepted(CoordinationOperationStatus.Current),
                    updated));
        }
    }

    public ValueTask<CoordinationOperationStatus> ReleasePlayerLeaseAsync(
        CoordinatedPlayerLease lease,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.FromResult(
                    CoordinationOperationStatus.Unavailable);
            }
            if (DeadlineExpired(deadline))
            {
                return ValueTask.FromResult(Timeout());
            }
            if (!_players.TryGetValue(
                    lease.CharacterId,
                    out var current))
            {
                return ValueTask.FromResult(
                    CoordinationOperationStatus.NotFound);
            }
            if (!SameOwner(current.Lease, lease))
            {
                return ValueTask.FromResult(Conflict());
            }

            RemovePlayer(current.Lease);
            return ValueTask.FromResult(
                Accepted(CoordinationOperationStatus.Applied));
        }
    }

    public ValueTask<PlayerLeaseLookup> FindPlayerLeaseAsync(
        int characterId,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (DeadlineExpired(deadline))
            {
                return ValueTask.FromResult(
                    new PlayerLeaseLookup(Timeout(), null));
            }

            CleanupExpired(_timeProvider.GetUtcNow());
            if (!_players.TryGetValue(characterId, out var entry))
            {
                return ValueTask.FromResult(
                    new PlayerLeaseLookup(
                        CoordinationOperationStatus.NotFound,
                        null));
            }

            return ValueTask.FromResult(
                new PlayerLeaseLookup(
                    Accepted(CoordinationOperationStatus.Current),
                    entry.Lease));
        }
    }

    private void RemoveAccountPlayer(
        int accountId,
        int exceptCharacterId)
    {
        if (_playerByAccount.TryGetValue(
                accountId,
                out var existingCharacterId) &&
            existingCharacterId != exceptCharacterId &&
            _players.TryGetValue(
                existingCharacterId,
                out var existing))
        {
            RemovePlayer(existing.Lease);
        }
    }

    private void RemovePlayer(CoordinatedPlayerLease lease)
    {
        _players.Remove(lease.CharacterId);
        if (_playerByAccount.TryGetValue(
                lease.AccountId,
                out var characterId) &&
            characterId == lease.CharacterId)
        {
            _playerByAccount.Remove(lease.AccountId);
        }
    }

    private static bool SameOwner(
        CoordinatedPlayerLease current,
        CoordinatedPlayerLease expected) =>
        current.AccountId == expected.AccountId &&
        current.CharacterId == expected.CharacterId &&
        current.Ownership == expected.Ownership &&
        current.LeaseToken == expected.LeaseToken &&
        current.NodeId == expected.NodeId &&
        current.WorkerBootId == expected.WorkerBootId;

    private sealed class PlayerEntry(CoordinatedPlayerLease lease)
    {
        public CoordinatedPlayerLease Lease { get; set; } = lease;
    }
}
