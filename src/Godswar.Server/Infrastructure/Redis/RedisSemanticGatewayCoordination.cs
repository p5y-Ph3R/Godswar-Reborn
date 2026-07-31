using System.Collections.Concurrent;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Gateway;

namespace Godswar.Server.Infrastructure.Redis;

/// <summary>
/// Disposable Redis authority for semantic-gateway login generations and
/// worker admissions. Redis owns no durable player value and cannot supply
/// gateway endpoints, certificates, routes, or worker trust.
/// </summary>
internal sealed partial class RedisSemanticGatewayCoordination :
    ISemanticGatewayCoordination
{
    private static readonly TimeSpan StateStorageTtl =
        TimeSpan.FromHours(25);

    private readonly ConcurrentDictionary<
        GatewayAdmissionId,
        SemanticGatewayAdmissionLease> _admissions = [];
    private readonly RedisCoordinationExecutor _executor;
    private readonly ConcurrentDictionary<
        int,
        SemanticGatewayLoginGenerationLease> _generations = [];
    private readonly RedisCoordinationKeyBuilder _keys;
    private readonly SemanticGatewayAuthorityLimits _limits;
    private readonly bool _ownsExecutor;
    private readonly bool _ownsWorkerRoutes;
    private readonly StaticSemanticGatewayRouteDirectory _routes;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerCoordination _workerRoutes;
    private long _admissionsCommitted;
    private long _admissionsExpired;
    private long _admissionsInvalidated;
    private long _admissionsRefreshed;
    private long _admissionsReleased;
    private long _admissionsReserved;
    private long _admissionsRolledBack;
    private long _bindingRejections;
    private long _capacityRejections;
    private int _disposed;
    private long _identityConflicts;
    private long _loginGenerationsStarted;
    private long _loginGenerationsSuperseded;
    private long _routeRejections;

    public RedisSemanticGatewayCoordination(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        StaticSemanticGatewayRouteDirectory routes,
        SemanticGatewayAuthorityLimits limits,
        TimeProvider? timeProvider = null,
        bool ownsExecutor = false)
        : this(
            executor,
            keys,
            routes,
            new RedisWorkerCoordination(
                executor,
                keys,
                limits?.MaximumAdmissions ??
                throw new ArgumentNullException(nameof(limits)),
                executor?.GetSnapshot().MaximumConcurrency ??
                throw new ArgumentNullException(nameof(executor))),
            limits,
            timeProvider,
            ownsExecutor,
            ownsWorkerRoutes: true)
    {
    }

    public RedisSemanticGatewayCoordination(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        StaticSemanticGatewayRouteDirectory routes,
        IWorkerCoordination workerRoutes,
        SemanticGatewayAuthorityLimits limits,
        TimeProvider? timeProvider = null,
        bool ownsExecutor = false,
        bool ownsWorkerRoutes = false)
    {
        _executor = executor ??
            throw new ArgumentNullException(nameof(executor));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _workerRoutes = workerRoutes ??
            throw new ArgumentNullException(nameof(workerRoutes));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        if (_limits.MaximumAdmissionsPerGeneration != 1)
        {
            throw new ArgumentException(
                "Redis semantic coordination requires a single-use login " +
                "generation.",
                nameof(limits));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsExecutor = ownsExecutor;
        _ownsWorkerRoutes = ownsWorkerRoutes;
    }

    public SemanticGatewayAuthoritySnapshot GetSnapshot()
    {
        var admissions = _admissions.Values.ToArray();
        var reserved = admissions.Count(static value =>
            value.State == SemanticGatewayAdmissionState.Reserved);
        var committed = admissions.Length - reserved;
        var routeSnapshot = _routes.GetSnapshot();
        var byWorker = admissions
            .GroupBy(static value => value.Route.NodeId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count());
        var workers = routeSnapshot.Workers
            .Select(worker => worker with
            {
                ActiveAdmissions = byWorker.GetValueOrDefault(worker.NodeId)
            })
            .ToArray();
        var observedRoutes = routeSnapshot with
        {
            ActiveReservations = admissions.Length,
            Workers = workers
        };

        return new SemanticGatewayAuthoritySnapshot(
            _generations.Count,
            _limits.MaximumLoginGenerations,
            reserved,
            committed,
            _limits.MaximumAdmissions,
            Interlocked.Read(ref _loginGenerationsStarted),
            Interlocked.Read(ref _loginGenerationsSuperseded),
            Interlocked.Read(ref _identityConflicts),
            Interlocked.Read(ref _admissionsReserved),
            Interlocked.Read(ref _admissionsCommitted),
            Interlocked.Read(ref _admissionsRefreshed),
            Interlocked.Read(ref _admissionsRolledBack),
            Interlocked.Read(ref _admissionsReleased),
            Interlocked.Read(ref _admissionsInvalidated),
            Interlocked.Read(ref _admissionsExpired),
            Interlocked.Read(ref _capacityRejections),
            Interlocked.Read(ref _routeRejections),
            Interlocked.Read(ref _bindingRejections),
            observedRoutes);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _generations.Clear();
        _admissions.Clear();
        if (_ownsWorkerRoutes)
        {
            await _workerRoutes.DisposeAsync();
        }
        if (_ownsExecutor)
        {
            await _executor.DisposeAsync();
        }
    }

    private static long TtlMilliseconds(TimeSpan value) =>
        checked((long)Math.Ceiling(value.TotalMilliseconds));

    private void CacheGeneration(
        SemanticGatewayLoginGenerationLease generation)
    {
        _generations[generation.Principal.AccountId] = generation;
    }

    private void RemoveObservedGeneration(
        GatewayLoginGenerationId generationId,
        int accountId)
    {
        if (_generations.TryGetValue(accountId, out var current) &&
            current.GenerationId == generationId)
        {
            _generations.TryRemove(
                new KeyValuePair<
                    int,
                    SemanticGatewayLoginGenerationLease>(
                    accountId,
                    current));
        }
        foreach (var admission in _admissions)
        {
            if (admission.Value.GenerationId == generationId)
            {
                _admissions.TryRemove(admission);
            }
        }
    }

    private void PruneObservedExpiry(DateTimeOffset now)
    {
        foreach (var generation in _generations)
        {
            if (generation.Value.ExpiresAt <= now)
            {
                RemoveObservedGeneration(
                    generation.Value.GenerationId,
                    generation.Key);
            }
        }
        foreach (var admission in _admissions)
        {
            if (admission.Value.ExpiresAt <= now)
            {
                _admissions.TryRemove(admission);
            }
        }
    }

    private void EnsureAvailable(
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.Remaining(_timeProvider) <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The semantic-gateway coordination deadline elapsed.");
        }
    }
}
