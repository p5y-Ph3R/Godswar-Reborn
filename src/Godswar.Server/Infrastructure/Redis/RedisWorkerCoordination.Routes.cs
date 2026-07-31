using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisWorkerCoordination
{
    private async ValueTask<CoordinationOperationStatus>
        RegisterRouteAsync(
            CoordinatedWorldRoute route,
            WorkerRegistrationLease lease,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Route,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisCoordinationScripts.RegisterRoute,
                [
                    _keys.Route(route.WorldInstanceId),
                    _keys.Worker(lease.NodeId)
                ],
                [
                    route.RealmId.Value,
                    route.MapId.Value,
                    route.WorldInstanceId.Value.ToString("N"),
                    lease.NodeId.ToString(),
                    lease.BootId.ToString("N"),
                    lease.Revision,
                    TtlMilliseconds(ttl)
                ]),
            cancellationToken);
        var status = RedisResultReader.Pair(result).Status == 1
            ? CoordinationOperationStatus.Applied
            : CoordinationOperationStatus.Conflict;
        _executor.RecordLogicalOutcome(
            RedisCoordinationOperationFamily.Route,
            status);
        return status;
    }

    private async ValueTask<CoordinationOperationStatus> RenewRouteAsync(
            CoordinatedWorldRoute route,
            WorkerRegistrationLease lease,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Route,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisCoordinationScripts.RenewRoute,
                [_keys.Route(route.WorldInstanceId)],
                [
                    lease.NodeId.ToString(),
                    lease.BootId.ToString("N"),
                    lease.Revision,
                    TtlMilliseconds(ttl)
                ]),
            cancellationToken);
        var status = RedisResultReader.Pair(result).Status switch
        {
            1 => CoordinationOperationStatus.Current,
            0 => CoordinationOperationStatus.NotFound,
            _ => CoordinationOperationStatus.Conflict
        };
        _executor.RecordLogicalOutcome(
            RedisCoordinationOperationFamily.Route,
            status);
        return status;
    }

    private async ValueTask ReleaseRouteAsync(
        CoordinatedWorldRoute route,
        WorkerRegistrationLease lease,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Route,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisCoordinationScripts.ReleaseExact,
                [_keys.Route(route.WorldInstanceId)],
                [
                    "boot",
                    "revision",
                    lease.BootId.ToString("N"),
                    lease.Revision
                ]),
            cancellationToken);
        _executor.RecordLogicalOutcome(
            RedisCoordinationOperationFamily.Route,
            RedisStatus(RedisResultReader.Integer(result)));
    }

    private async ValueTask RollbackRegistrationAsync(
        WorkerRegistrationLease lease,
        IReadOnlyList<CoordinatedWorldRoute> routes,
        CoordinationDeadline deadline)
    {
        foreach (var route in routes)
        {
            try
            {
                await ReleaseRouteAsync(
                    route,
                    lease,
                    deadline,
                    CancellationToken.None);
            }
            catch
            {
            }
        }
        try
        {
            var result = await _executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Worker,
                deadline,
                database => database.ScriptEvaluateAsync(
                    RedisCoordinationScripts.ReleaseExact,
                    [_keys.Worker(lease.NodeId)],
                    [
                        "boot",
                        "revision",
                        lease.BootId.ToString("N"),
                        lease.Revision
                    ]),
                CancellationToken.None);
            _executor.RecordLogicalOutcome(
                RedisCoordinationOperationFamily.Worker,
                RedisStatus(RedisResultReader.Integer(result)));
        }
        catch
        {
        }
    }

    private WorkerRegistrationResult WorkerFailure(
        CoordinationOperationStatus status)
        => WorkerResult(status, null);

    private WorkerRegistrationResult WorkerResult(
        CoordinationOperationStatus status,
        WorkerRegistrationLease? lease)
    {
        _executor.RecordLogicalOutcome(
            RedisCoordinationOperationFamily.Worker,
            status);
        return new WorkerRegistrationResult(status, lease);
    }

    private CoordinatedRouteLookup RouteLookup(
        CoordinationOperationStatus status,
        CoordinatedRouteSnapshot? route)
    {
        _executor.RecordLogicalOutcome(
            RedisCoordinationOperationFamily.Route,
            status);
        return new CoordinatedRouteLookup(status, route);
    }

    private static CoordinatedWorldRoute ReadRoute(
        RedisHashReader hash) =>
        new(
            new RealmId(hash.RequiredInt32("realm")),
            new MapId(hash.RequiredInt16("map")),
            new WorldInstanceId(hash.RequiredGuid("world")));

    private static DateTimeOffset MinimumUntil(
        RedisHashReader left,
        RedisHashReader right)
    {
        var leftUntil = left.RequiredDateTimeOffset("until");
        var rightUntil = right.RequiredDateTimeOffset("until");
        return leftUntil <= rightUntil ? leftUntil : rightUntil;
    }

    private static CoordinationOperationStatus RedisStatus(long value) =>
        value switch
        {
            1 => CoordinationOperationStatus.Applied,
            0 => CoordinationOperationStatus.NotFound,
            _ => CoordinationOperationStatus.Conflict
        };

    private static long TtlMilliseconds(TimeSpan value) =>
        checked((long)Math.Ceiling(value.TotalMilliseconds));

    private static DateTimeOffset RedisTimestamp(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException(
                "Redis returned an invalid lease timestamp.",
                error);
        }
    }

    private static void ValidateTtl(TimeSpan ttl)
    {
        if (ttl < TimeSpan.FromSeconds(1) ||
            ttl > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(ttl));
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}
