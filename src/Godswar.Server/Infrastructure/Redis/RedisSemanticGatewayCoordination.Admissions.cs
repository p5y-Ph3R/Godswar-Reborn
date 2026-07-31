using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Domain.World.Instances;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisSemanticGatewayCoordination
{
    public async ValueTask<SemanticGatewayAdmissionResult>
        ReserveAdmissionAsync(
            GatewayLoginGenerationId generationId,
            SemanticGatewayPrincipal principal,
            SemanticGatewayConnectionSource source,
            SemanticGatewayRouteTarget target,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        RequireAdmissionRequest(
            generationId,
            principal,
            source,
            target);
        EnsureAvailable(deadline, cancellationToken);
        var routeProof = await ResolveRouteProofAsync(
            target,
            established: false,
            expectedNode: null,
            expectedProof: null,
            deadline,
            cancellationToken);
        var selection = routeProof.Selection;
        var capacities = routeProof.Capacities;
        var coordinated = routeProof.CoordinatedRoute;

        var admissionId = GatewayAdmissionId.New();
        var connectionKey = _keys.LoginConnection(
            source.ConnectionId.Value);
        var routeField =
            _keys.GatewayRouteCounterField(target.WorldInstanceId);
        var workerField = selection is null
            ? "worker-unselected"
            : _keys.GatewayWorkerCounterField(selection.NodeId);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisSemanticGatewayScripts.ReserveAdmission,
                [
                    _keys.LoginAccount(principal.AccountId),
                    _keys.Admission(admissionId.Value),
                    connectionKey,
                    _keys.GatewayCounters(),
                    _keys.GatewayExpiry(),
                    _keys.LoginName(principal.CanonicalUsername!),
                    _keys.Route(target.WorldInstanceId),
                    _keys.Worker(
                        selection?.NodeId ?? new ServerNodeId("unselected"))
                ],
                [
                    generationId.ToString(),
                    principal.AccountId,
                    principal.CanonicalUsername!,
                    source.ConnectionId.ToString(),
                    source.Address!.ToString(),
                    target.RealmId.Value,
                    target.MapId.Value,
                    target.WorldInstanceId.Value.ToString("N"),
                    selection?.NodeId.ToString() ?? "-",
                    selection?.WorkerRevision ?? 0,
                    admissionId.ToString(),
                    TtlMilliseconds(_limits.ReservationTtl),
                    TtlMilliseconds(StateStorageTtl),
                    Math.Min(
                        _limits.MaximumAdmissions,
                        _routes.MaximumAdmissions),
                    _limits.MaximumAdmissionsPerGeneration,
                    capacities?.Worker ?? 0,
                    capacities?.Route ?? 0,
                    routeField,
                    workerField,
                    (int)routeProof.Status,
                    coordinated?.BootId.ToString("N") ?? "-",
                    coordinated?.Revision ?? 0
                ]),
            cancellationToken);
        var values =
            RedisSemanticGatewayResultReader.Array(result, 5);
        var status = AdmissionStatus(values[0]);
        if (status != SemanticGatewayAdmissionStatus.Reserved)
        {
            SemanticGatewayRouteSelectionStatus? rejection = status ==
                SemanticGatewayAdmissionStatus.RouteRejected
                    ? RouteStatus(values[4])
                    : null;
            RecordAdmissionRejection(status);
            if (rejection is not null)
            {
                Interlocked.Increment(ref _routeRejections);
            }
            return new(status, null, rejection);
        }
        if (selection is null)
        {
            throw new InvalidDataException(
                "Redis admitted an untrusted static route.");
        }

        var lease = new SemanticGatewayAdmissionLease(
            admissionId,
            generationId,
            principal,
            source,
            selection,
            SemanticGatewayAdmissionState.Reserved,
            RedisSemanticGatewayResultReader.Timestamp(values[1]),
            RedisSemanticGatewayResultReader.Timestamp(values[2]));
        _admissions[admissionId] = lease;
        Interlocked.Increment(ref _admissionsReserved);
        return new(status, lease);
    }

    public async ValueTask<SemanticGatewayAdmissionResult>
        CommitAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        RequireClaim(claim);
        EnsureAvailable(deadline, cancellationToken);
        var trusted = await ResolveRouteProofAsync(
            claim.Target,
            established: false,
            claim.NodeId,
            claim.WorkerRevision,
            deadline,
            cancellationToken);
        if (!trusted.IsSelected)
        {
            var removed = await RemoveAdmissionCoreAsync(
                claim,
                SemanticGatewayAdmissionState.Reserved,
                SemanticGatewayAdmissionStatus.RolledBack,
                deadline,
                cancellationToken);
            if (removed.Status !=
                SemanticGatewayAdmissionStatus.RolledBack)
            {
                return removed;
            }

            Interlocked.Increment(ref _routeRejections);
            return new(
                SemanticGatewayAdmissionStatus.RouteRejected,
                null,
                trusted.Status);
        }

        var result = await ExecuteProvenClaimScriptAsync(
            RedisSemanticGatewayScripts.CommitAdmission,
            claim,
            trusted,
            [
                TtlMilliseconds(_limits.CommittedAdmissionTtl),
                TtlMilliseconds(StateStorageTtl)
            ],
            deadline,
            cancellationToken);
        var parsed = ParseProvenAdmissionResult(result, claim);
        ObserveAdmissionResult(parsed, claim);
        return parsed;
    }

    public async ValueTask<SemanticGatewayAdmissionResult>
        RefreshAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        RequireClaim(claim);
        EnsureAvailable(deadline, cancellationToken);
        var current = await ResolveAdmissionAsync(
            claim,
            deadline,
            cancellationToken);
        if (current.Status !=
            SemanticGatewayAdmissionStatus.Committed)
        {
            return current;
        }
        var trusted = await ResolveRouteProofAsync(
            claim.Target,
            established: true,
            claim.NodeId,
            claim.WorkerRevision,
            deadline,
            cancellationToken);
        if (!trusted.IsSelected)
        {
            Interlocked.Increment(ref _routeRejections);
            return new(
                SemanticGatewayAdmissionStatus.RouteRejected,
                null,
                trusted.Status);
        }

        var result = await ExecuteProvenClaimScriptAsync(
            RedisSemanticGatewayScripts.RefreshAdmission,
            claim,
            trusted,
            [
                TtlMilliseconds(_limits.CommittedAdmissionTtl),
                TtlMilliseconds(_limits.LoginGenerationTtl),
                TtlMilliseconds(StateStorageTtl)
            ],
            deadline,
            cancellationToken);
        var refresh = ParseRefreshAdmissionResult(result, claim);
        var parsed = refresh.AdmissionResult;
        ObserveAdmissionResult(parsed, claim);
        if (parsed.Status ==
                SemanticGatewayAdmissionStatus.Refreshed &&
            parsed.Admission is { } refreshed &&
            _generations.TryGetValue(
                claim.Principal.AccountId,
                out var generation) &&
            generation.GenerationId == claim.GenerationId)
        {
            CacheGeneration(
                generation with
                {
                    ExpiresAt = refresh.GenerationExpiresAt
                });
        }
        return parsed;
    }

    public async ValueTask<SemanticGatewayAdmissionResult>
        ResolveAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        RequireClaim(claim);
        EnsureAvailable(deadline, cancellationToken);
        var result = await ExecuteClaimScriptAsync(
            RedisSemanticGatewayScripts.ResolveAdmission,
            claim,
            [],
            deadline,
            cancellationToken);
        var parsed = ParseAdmissionResult(result, claim);
        ObserveAdmissionResult(parsed, claim, countSuccess: false);
        return parsed;
    }

    public ValueTask<SemanticGatewayAdmissionResult>
        RollbackAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken) =>
        RemoveAdmissionCoreAsync(
            claim,
            SemanticGatewayAdmissionState.Reserved,
            SemanticGatewayAdmissionStatus.RolledBack,
            deadline,
            cancellationToken);

    public ValueTask<SemanticGatewayAdmissionResult>
        ReleaseAdmissionAsync(
            SemanticGatewayAdmissionClaim claim,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken) =>
        RemoveAdmissionCoreAsync(
            claim,
            SemanticGatewayAdmissionState.Committed,
            SemanticGatewayAdmissionStatus.Released,
            deadline,
            cancellationToken);

    public async ValueTask<int> SweepExpiredAsync(
        CoordinationDeadline deadline,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(deadline, cancellationToken);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            deadline,
            database => database.ScriptEvaluateAsync(
                RedisSemanticGatewayScripts.SweepExpired,
                [
                    _keys.GatewayExpiry(),
                    _keys.GatewayCounters()
                ],
                [
                    _limits.MaximumExpiryWorkPerOperation,
                    TtlMilliseconds(StateStorageTtl)
                ]),
            cancellationToken);
        var values =
            RedisSemanticGatewayResultReader.Array(result, 4);
        var processed =
            RedisSemanticGatewayResultReader.Int32(values[0]);
        var admissions =
            RedisSemanticGatewayResultReader.Int32(values[1]);
        if (admissions > 0)
        {
            Interlocked.Add(ref _admissionsExpired, admissions);
        }
        PruneObservedExpiry(
            RedisSemanticGatewayResultReader.Timestamp(values[3]));
        return processed;
    }

    private async ValueTask<SemanticGatewayAdmissionResult>
        RemoveAdmissionCoreAsync(
            SemanticGatewayAdmissionClaim claim,
            SemanticGatewayAdmissionState expectedState,
            SemanticGatewayAdmissionStatus successStatus,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken)
    {
        RequireClaim(claim);
        EnsureAvailable(deadline, cancellationToken);
        var result = await ExecuteClaimScriptAsync(
            RedisSemanticGatewayScripts.RemoveAdmission,
            claim,
            [
                (int)expectedState,
                (int)successStatus
            ],
            deadline,
            cancellationToken);
        var parsed = ParseAdmissionResult(result, claim);
        ObserveAdmissionResult(parsed, claim);
        return parsed;
    }

}
