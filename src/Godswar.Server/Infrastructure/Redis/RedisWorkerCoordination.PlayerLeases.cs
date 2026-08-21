using System.Globalization;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisWorkerCoordination
{
    public async ValueTask<PlayerLeaseResult> InstallPlayerLeaseAsync(
        PlayerLeaseInstallRequest request,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ValidateTtl(ttl);
        ThrowIfDisposed();

        try
        {
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Player,
                deadline,
                database => database.ScriptEvaluateAsync(
                    RedisCoordinationScripts.InstallPlayer,
                    [
                        _keys.Player(request.CharacterId),
                        _keys.Route(request.Route.WorldInstanceId),
                        _keys.Worker(request.NodeId),
                        _keys.PlayerAccount(request.AccountId)
                    ],
                    [
                        request.AccountId,
                        request.CharacterId,
                        request.Ownership.OwnerId.ToString("N"),
                        request.Ownership.Generation,
                        request.LeaseToken.ToString("N"),
                        request.NodeId.ToString(),
                        request.WorkerBootId.ToString("N"),
                        request.Route.RealmId.Value,
                        request.Route.MapId.Value,
                        request.Route.WorldInstanceId.Value.ToString("N"),
                        (int)request.Presence,
                        TtlMilliseconds(ttl),
                        request.AllowAccountReplacement ? 1 : 0
                    ]),
                cancellationToken);
            var response = RedisResultReader.Triple(result);
            if (response.Status <= 0)
            {
                return PlayerFailure(
                    response.Status == 0
                        ? CoordinationOperationStatus.NotFound
                        : CoordinationOperationStatus.Conflict);
            }
            if (response.Status is not (1 or 2) ||
                response.Value <= 0)
            {
                return PlayerFailure(
                    CoordinationOperationStatus.Unavailable);
            }
            var until = RedisTimestamp(response.Timestamp);

            var lease = new CoordinatedPlayerLease(
                request.AccountId,
                request.CharacterId,
                request.Ownership,
                request.LeaseToken,
                request.NodeId,
                request.WorkerBootId,
                request.Route,
                request.Presence,
                response.Value,
                until);
            if (response.Status == 1)
            {
                IncrementActivePlayerLeases();
            }
            return PlayerResult(
                response.Status == 1
                    ? CoordinationOperationStatus.Applied
                    : CoordinationOperationStatus.Current,
                lease);
        }
        catch (RedisCoordinationException error)
        {
            return PlayerFailure(error.Status);
        }
    }

    public async ValueTask<PlayerLeaseResult> RenewPlayerLeaseAsync(
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
        ThrowIfDisposed();

        try
        {
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Player,
                deadline,
                database => database.ScriptEvaluateAsync(
                    RedisCoordinationScripts.RenewPlayer,
                    [
                        _keys.Player(lease.CharacterId),
                        _keys.Route(route.WorldInstanceId),
                        _keys.Worker(lease.NodeId),
                        _keys.PlayerAccount(lease.AccountId)
                    ],
                    [
                        lease.AccountId,
                        lease.CharacterId,
                        lease.Ownership.OwnerId.ToString("N"),
                        lease.Ownership.Generation,
                        lease.LeaseToken.ToString("N"),
                        lease.NodeId.ToString(),
                        lease.WorkerBootId.ToString("N"),
                        route.RealmId.Value,
                        route.MapId.Value,
                        route.WorldInstanceId.Value.ToString("N"),
                        (int)presence,
                        TtlMilliseconds(ttl)
                    ]),
                cancellationToken);
            var response = RedisResultReader.Triple(result);
            if (response.Status <= 0)
            {
                return PlayerFailure(
                    response.Status == 0
                        ? CoordinationOperationStatus.NotFound
                        : CoordinationOperationStatus.Conflict);
            }
            var until = RedisTimestamp(response.Timestamp);

            return PlayerResult(
                CoordinationOperationStatus.Current,
                lease with
                {
                    Route = route,
                    Presence = presence,
                    Version = response.Value,
                    ProvenUntilUtc = until
                });
        }
        catch (RedisCoordinationException error)
        {
            return PlayerFailure(error.Status);
        }
    }

    public async ValueTask<CoordinationOperationStatus>
        ReleasePlayerLeaseAsync(
            CoordinatedPlayerLease lease,
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
        ThrowIfDisposed();

        try
        {
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Player,
                deadline,
                database => database.ScriptEvaluateAsync(
                    RedisCoordinationScripts.ReleasePlayer,
                    [
                        _keys.Player(lease.CharacterId),
                        _keys.PlayerAccount(lease.AccountId)
                    ],
                    [
                        lease.AccountId,
                        lease.CharacterId,
                        lease.Ownership.OwnerId.ToString("N"),
                        lease.Ownership.Generation,
                        lease.LeaseToken.ToString("N"),
                        lease.NodeId.ToString(),
                        lease.WorkerBootId.ToString("N")
                    ]),
                cancellationToken);
            var status = RedisStatus(
                RedisResultReader.Integer(result));
            if (status == CoordinationOperationStatus.Applied)
            {
                DecrementActivePlayerLeases();
            }
            _executor.RecordLogicalOutcome(
                RedisCoordinationOperationFamily.Player,
                status);
            return status;
        }
        catch (RedisCoordinationException error)
        {
            _executor.RecordLogicalOutcome(
                RedisCoordinationOperationFamily.Player,
                error.Status);
            return error.Status;
        }
    }

    public async ValueTask<PlayerLeaseLookup> FindPlayerLeaseAsync(
        int characterId,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        ThrowIfDisposed();
        try
        {
            var entries = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Player,
                deadline,
                database => database.HashGetAllAsync(
                    _keys.Player(characterId)),
                cancellationToken);
            if (entries.Length == 0)
            {
                return PlayerLookup(
                    CoordinationOperationStatus.NotFound,
                    null);
            }

            var lease = ReadPlayerLease(
                new RedisHashReader(entries));
            return lease.CharacterId == characterId
                ? PlayerLookup(
                    CoordinationOperationStatus.Current,
                    lease)
                : PlayerLookup(
                    CoordinationOperationStatus.Conflict,
                    null);
        }
        catch (RedisCoordinationException error)
        {
            return PlayerLookup(error.Status, null);
        }
        catch (Exception error)
            when (error is InvalidDataException or
                ArgumentException or
                OverflowException)
        {
            return PlayerLookup(
                CoordinationOperationStatus.Unavailable,
                null);
        }
    }

    private PlayerLeaseResult PlayerFailure(
        CoordinationOperationStatus status)
        => PlayerResult(status, null);

    private PlayerLeaseResult PlayerResult(
        CoordinationOperationStatus status,
        CoordinatedPlayerLease? lease)
    {
        _executor.RecordLogicalOutcome(
            RedisCoordinationOperationFamily.Player,
            status);
        return new PlayerLeaseResult(status, lease);
    }

    private PlayerLeaseLookup PlayerLookup(
        CoordinationOperationStatus status,
        CoordinatedPlayerLease? lease)
    {
        _executor.RecordLogicalOutcome(
            RedisCoordinationOperationFamily.Player,
            status);
        return new PlayerLeaseLookup(status, lease);
    }

    private void IncrementActivePlayerLeases()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activePlayerLeases);
            if (current >= _capacity)
            {
                return;
            }
            if (Interlocked.CompareExchange(
                    ref _activePlayerLeases,
                    current + 1,
                    current) == current)
            {
                return;
            }
        }
    }

    private void DecrementActivePlayerLeases()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activePlayerLeases);
            if (current <= 0)
            {
                return;
            }
            if (Interlocked.CompareExchange(
                    ref _activePlayerLeases,
                    current - 1,
                    current) == current)
            {
                return;
            }
        }
    }

    private static CoordinatedPlayerLease ReadPlayerLease(
        RedisHashReader hash) =>
        new(
            hash.RequiredInt32("account"),
            hash.RequiredInt32("character"),
            new PlayerOwnershipFence(
                hash.RequiredGuid("owner"),
                hash.RequiredInt64("generation")),
            hash.RequiredGuid("token"),
            new ServerNodeId(hash.RequiredString("node", 64)),
            hash.RequiredGuid("boot"),
            new CoordinatedWorldRoute(
                new RealmId(hash.RequiredInt32("realm")),
                new MapId(hash.RequiredInt16("map")),
                new WorldInstanceId(hash.RequiredGuid("world"))),
            (CoordinatedPresenceState)hash.RequiredByte("presence"),
            hash.RequiredInt64("version"),
            hash.RequiredDateTimeOffset("until"));
}
