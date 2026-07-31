using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Infrastructure.Redis;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisSemanticGatewayCoordinationIntegrationChecks
{
    private static async Task CheckAtomicRouteProofTransitionsAsync(
        RedisSemanticGatewayCoordination gateway,
        RedisWorkerCoordination worker,
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        SemanticGatewayAuthorityLimits limits)
    {
        var registered = await worker.RegisterWorkerAsync(
            Registration(Guid.NewGuid()),
            TimeSpan.FromSeconds(5),
            Deadline(),
            CancellationToken.None);
        Check.True(
            registered.Succeeded && registered.Lease is not null,
            "atomic route-proof fixture publishes an available worker");
        var current = registered.Lease!.Value;

        var reservePrincipal =
            new SemanticGatewayPrincipal(705, "ATOMIC_RESERVE");
        var reserveLogin = await StartActivatedAsync(
            gateway,
            reservePrincipal,
            "192.0.2.101");
        var reserveRace = new FindRouteRaceCoordination(
            worker,
            async () =>
            {
                var draining = await worker.RenewWorkerAsync(
                    current,
                    CoordinatedWorkerState.Draining,
                    TimeSpan.FromSeconds(5),
                    Deadline(),
                    CancellationToken.None);
                Check.True(
                    draining.Succeeded && draining.Lease is not null,
                    "reserve race moves the proven worker to draining");
                current = draining.Lease!.Value;
            });
        await using (var raceGateway =
            new RedisSemanticGatewayCoordination(
                executor,
                keys,
                Directory(),
                reserveRace,
                limits))
        {
            var rejected = await raceGateway.ReserveAdmissionAsync(
                reserveLogin.GenerationId,
                reservePrincipal,
                Source("192.0.2.102"),
                Target(),
                Deadline(),
                CancellationToken.None);
            Check.True(
                rejected.Status ==
                    SemanticGatewayAdmissionStatus.RouteRejected &&
                rejected.RouteRejection ==
                    SemanticGatewayRouteSelectionStatus.WorkerDraining,
                "reserve Lua rejects a drain that occurs after route lookup");
        }
        _ = await gateway.CancelLoginAsync(
            reserveLogin,
            Deadline(),
            CancellationToken.None);
        current = RequireLease(
            await worker.RenewWorkerAsync(
                current,
                CoordinatedWorkerState.Available,
                TimeSpan.FromSeconds(5),
                Deadline(),
                CancellationToken.None),
            "reserve race restores the worker to available");

        var commitPrincipal =
            new SemanticGatewayPrincipal(706, "ATOMIC_COMMIT");
        var commitLogin = await StartActivatedAsync(
            gateway,
            commitPrincipal,
            "192.0.2.103");
        var reservation = await gateway.ReserveAdmissionAsync(
            commitLogin.GenerationId,
            commitPrincipal,
            Source("192.0.2.104"),
            Target(),
            Deadline(),
            CancellationToken.None);
        Check.True(
            reservation.Status ==
                SemanticGatewayAdmissionStatus.Reserved &&
            reservation.Admission is not null,
            "commit race fixture reserves an exact route");
        var commitClaim = Claim(reservation.Admission!);
        var commitRace = new FindRouteRaceCoordination(
            worker,
            async () =>
            {
                var draining = await worker.RenewWorkerAsync(
                    current,
                    CoordinatedWorkerState.Draining,
                    TimeSpan.FromSeconds(5),
                    Deadline(),
                    CancellationToken.None);
                current = RequireLease(
                    draining,
                    "commit race moves the worker to draining");
            });
        await using (var raceGateway =
            new RedisSemanticGatewayCoordination(
                executor,
                keys,
                Directory(),
                commitRace,
                limits))
        {
            var rejected = await raceGateway.CommitAdmissionAsync(
                commitClaim,
                Deadline(),
                CancellationToken.None);
            Check.True(
                rejected.Status ==
                    SemanticGatewayAdmissionStatus.RouteRejected &&
                rejected.RouteRejection ==
                    SemanticGatewayRouteSelectionStatus.WorkerDraining,
                "commit Lua rejects a drain that occurs after route lookup");
        }
        _ = await gateway.RollbackAdmissionAsync(
            commitClaim,
            Deadline(),
            CancellationToken.None);
        _ = await gateway.CancelLoginAsync(
            commitLogin,
            Deadline(),
            CancellationToken.None);
        current = RequireLease(
            await worker.RenewWorkerAsync(
                current,
                CoordinatedWorkerState.Available,
                TimeSpan.FromSeconds(5),
                Deadline(),
                CancellationToken.None),
            "commit race restores the worker to available");

        var refreshPrincipal =
            new SemanticGatewayPrincipal(707, "ATOMIC_REFRESH");
        var refreshLogin = await StartActivatedAsync(
            gateway,
            refreshPrincipal,
            "192.0.2.105");
        var refreshReservation = await gateway.ReserveAdmissionAsync(
            refreshLogin.GenerationId,
            refreshPrincipal,
            Source("192.0.2.106"),
            Target(),
            Deadline(),
            CancellationToken.None);
        var refreshClaim = Claim(
            refreshReservation.Admission ??
            throw new InvalidOperationException(
                "Refresh race reservation returned no admission."));
        Check.True(
            (await gateway.CommitAdmissionAsync(
                refreshClaim,
                Deadline(),
                CancellationToken.None)).Status ==
                SemanticGatewayAdmissionStatus.Committed,
            "refresh race fixture commits an exact route");

        WorkerRegistrationLease replacement = default;
        var refreshRace = new FindRouteRaceCoordination(
            worker,
            async () =>
            {
                Check.True(
                    await worker.ReleaseWorkerAsync(
                        current,
                        Deadline(),
                        CancellationToken.None) ==
                        CoordinationOperationStatus.Applied,
                    "refresh race releases the proven worker incarnation");
                replacement = RequireLease(
                    await worker.RegisterWorkerAsync(
                        Registration(Guid.NewGuid()),
                        TimeSpan.FromSeconds(5),
                        Deadline(),
                        CancellationToken.None),
                    "refresh race installs a replacement worker boot");
            });
        await using (var raceGateway =
            new RedisSemanticGatewayCoordination(
                executor,
                keys,
                Directory(),
                refreshRace,
                limits))
        {
            var rejected = await raceGateway.RefreshAdmissionAsync(
                refreshClaim,
                Deadline(),
                CancellationToken.None);
            Check.True(
                rejected.Status ==
                    SemanticGatewayAdmissionStatus.RouteRejected &&
                rejected.RouteRejection ==
                    SemanticGatewayRouteSelectionStatus
                        .RouteIdentityMismatch,
                "refresh Lua rejects reassignment after route lookup");
        }

        _ = await gateway.ReleaseAdmissionAsync(
            refreshClaim,
            Deadline(),
            CancellationToken.None);
        _ = await gateway.CancelLoginAsync(
            refreshLogin,
            Deadline(),
            CancellationToken.None);
        _ = await worker.ReleaseWorkerAsync(
            replacement,
            Deadline(),
            CancellationToken.None);
    }

    private static WorkerRegistrationLease RequireLease(
        WorkerRegistrationResult result,
        string assertion)
    {
        Check.True(
            result.Succeeded && result.Lease is not null,
            assertion);
        return result.Lease!.Value;
    }

    private sealed class FindRouteRaceCoordination(
        IWorkerCoordination inner,
        Func<ValueTask> afterLookup) :
        IWorkerCoordination
    {
        private int _triggered;

        public async ValueTask<CoordinatedRouteLookup> FindRouteAsync(
            CoordinatedWorldRoute route,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.FindRouteAsync(
                route,
                deadline,
                cancellationToken);
            if (Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                await afterLookup();
            }
            return result;
        }

        public ValueTask<WorkerRegistrationResult> RegisterWorkerAsync(
            WorkerRegistrationRequest request,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.RegisterWorkerAsync(request, ttl, deadline, cancellationToken);

        public ValueTask<WorkerRegistrationResult> RenewWorkerAsync(
            WorkerRegistrationLease lease,
            CoordinatedWorkerState state,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.RenewWorkerAsync(
                lease,
                state,
                ttl,
                deadline,
                cancellationToken);

        public ValueTask<CoordinationOperationStatus> ReleaseWorkerAsync(
            WorkerRegistrationLease lease,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseWorkerAsync(lease, deadline, cancellationToken);

        public ValueTask<PlayerLeaseResult> InstallPlayerLeaseAsync(
            PlayerLeaseInstallRequest request,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.InstallPlayerLeaseAsync(
                request,
                ttl,
                deadline,
                cancellationToken);

        public ValueTask<PlayerLeaseResult> RenewPlayerLeaseAsync(
            CoordinatedPlayerLease lease,
            CoordinatedWorldRoute route,
            CoordinatedPresenceState presence,
            TimeSpan ttl,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.RenewPlayerLeaseAsync(
                lease,
                route,
                presence,
                ttl,
                deadline,
                cancellationToken);

        public ValueTask<CoordinationOperationStatus>
            ReleasePlayerLeaseAsync(
                CoordinatedPlayerLease lease,
                CoordinationDeadline deadline,
                CancellationToken cancellationToken = default) =>
            inner.ReleasePlayerLeaseAsync(
                lease,
                deadline,
                cancellationToken);

        public ValueTask<PlayerLeaseLookup> FindPlayerLeaseAsync(
            int characterId,
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.FindPlayerLeaseAsync(
                characterId,
                deadline,
                cancellationToken);

        public ValueTask<bool> CheckHealthAsync(
            CoordinationDeadline deadline,
            CancellationToken cancellationToken = default) =>
            inner.CheckHealthAsync(deadline, cancellationToken);

        public WorkerCoordinationSnapshot GetSnapshot() =>
            inner.GetSnapshot();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
