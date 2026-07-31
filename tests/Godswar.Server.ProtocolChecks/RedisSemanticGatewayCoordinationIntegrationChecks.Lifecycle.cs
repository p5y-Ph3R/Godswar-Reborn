using Godswar.Server.Application.Coordination;
using Godswar.Server.Infrastructure.Redis;
using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisSemanticGatewayCoordinationIntegrationChecks
{
    private static async Task CheckRollbackCleanupAsync(
        RedisSemanticGatewayCoordination gatewayA,
        RedisSemanticGatewayCoordination gatewayB,
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys)
    {
        var principal =
            new SemanticGatewayPrincipal(703, "ROLLBACK_STATE");
        var generation = await StartActivatedAsync(
            gatewayA,
            principal,
            "192.0.2.91");
        var gameSource = Source("192.0.2.92");
        var reserved = await gatewayA.ReserveAdmissionAsync(
            generation.GenerationId,
            principal,
            gameSource,
            Target(),
            Deadline(),
            CancellationToken.None);
        Check.True(
            reserved.Status ==
                SemanticGatewayAdmissionStatus.Reserved &&
            (await gatewayB.RollbackAdmissionAsync(
                Claim(reserved.Admission!),
                Deadline(),
                CancellationToken.None)).Status ==
                SemanticGatewayAdmissionStatus.RolledBack,
            "reservation rolls back through another gateway");
        Check.True(
            (await gatewayB.FindActivatedLoginAsync(
                principal.CanonicalUsername!,
                generation.LoginSource.Address!,
                Deadline(),
                CancellationToken.None)).IsFound,
            "rollback preserves the activated login-name mapping");

        var connectionExists = await executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.KeyExistsAsync(
                keys.LoginConnection(
                    gameSource.ConnectionId.Value)));
        var nameExists = await executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.KeyExistsAsync(
                keys.LoginName(principal.CanonicalUsername!)));
        var expiryScore = await executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.SortedSetScoreAsync(
                keys.GatewayExpiry(),
                "a|" + keys.LoginAccount(principal.AccountId)));
        var counters = await executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.HashGetAsync(
                keys.GatewayCounters(),
                [
                    "admissions",
                    "reserved",
                    keys.GatewayRouteCounterField(World),
                    keys.GatewayWorkerCounterField(Node)
                ]));
        Check.True(
            !connectionExists &&
            nameExists &&
            expiryScore is null &&
            counters.All(static value =>
                value.IsNull || value == 0),
            "rollback removes connection/expiry state and decrements all " +
            "admission counters without deleting login identity");

        var reusedConnection = await gatewayB.StartLoginAsync(
            new SemanticGatewayPrincipal(704, "REUSED_CONNECTION"),
            gameSource,
            Deadline(),
            CancellationToken.None);
        Check.True(
            reusedConnection.IsStarted,
            "rollback releases the exact game-connection index");
        _ = await gatewayA.CancelLoginAsync(
            reusedConnection.Generation!,
            Deadline(),
            CancellationToken.None);

        var replacement = await StartActivatedAsync(
            gatewayB,
            principal,
            "192.0.2.93");
        var capacityReused = await gatewayA.ReserveAdmissionAsync(
            replacement.GenerationId,
            principal,
            Source("192.0.2.94"),
            Target(),
            Deadline(),
            CancellationToken.None);
        Check.True(
            capacityReused.Status ==
                SemanticGatewayAdmissionStatus.Reserved,
            "rollback makes bounded route and worker capacity reusable");
        var capacityClaim = Claim(capacityReused.Admission!);
        Check.True(
            (await gatewayB.CommitAdmissionAsync(
                capacityClaim,
                Deadline(),
                CancellationToken.None)).Status ==
                SemanticGatewayAdmissionStatus.Committed &&
            (await gatewayA.ReleaseAdmissionAsync(
                capacityClaim,
                Deadline(),
                CancellationToken.None)).Status ==
                SemanticGatewayAdmissionStatus.Released,
            "committed admission releases across gateway adapters");
        _ = await gatewayA.CancelLoginAsync(
            replacement,
            Deadline(),
            CancellationToken.None);
    }

    private static async Task CheckDrainingAndBootFenceAsync(
        RedisSemanticGatewayCoordination gatewayA,
        RedisSemanticGatewayCoordination gatewayB,
        RedisWorkerCoordination worker,
        WorkerRegistrationLease availableLease,
        SemanticGatewayPrincipal principal)
    {
        var draining = await worker.RenewWorkerAsync(
            availableLease,
            CoordinatedWorkerState.Draining,
            TimeSpan.FromSeconds(5),
            Deadline(),
            CancellationToken.None);
        Check.True(
            draining.Succeeded,
            "worker heartbeat enters draining state");
        var drainingLogin = await StartActivatedAsync(
            gatewayA,
            principal,
            "192.0.2.71");
        var rejected = await gatewayB.ReserveAdmissionAsync(
            drainingLogin.GenerationId,
            principal,
            Source("192.0.2.72"),
            Target(),
            Deadline(),
            CancellationToken.None);
        Check.True(
            rejected.Status ==
                SemanticGatewayAdmissionStatus.RouteRejected &&
            rejected.RouteRejection ==
                SemanticGatewayRouteSelectionStatus.WorkerDraining,
            "draining Redis heartbeat excludes new admissions");

        var available = await worker.RenewWorkerAsync(
            draining.Lease ??
            throw new InvalidOperationException(
                "Draining renewal returned no lease."),
            CoordinatedWorkerState.Available,
            TimeSpan.FromSeconds(5),
            Deadline(),
            CancellationToken.None);
        Check.True(
            available.Succeeded,
            "worker heartbeat returns to available");
        var reservation = await gatewayA.ReserveAdmissionAsync(
            drainingLogin.GenerationId,
            principal,
            Source("192.0.2.73"),
            Target(),
            Deadline(),
            CancellationToken.None);
        Check.True(
            reservation.Status ==
                SemanticGatewayAdmissionStatus.Reserved,
            "available heartbeat admits the exact static route");

        Check.True(
            await worker.ReleaseWorkerAsync(
                available.Lease ??
                throw new InvalidOperationException(
                    "Available renewal returned no lease."),
                Deadline(),
                CancellationToken.None) ==
                CoordinationOperationStatus.Applied,
            "old worker incarnation releases its route");
        var replacement = await worker.RegisterWorkerAsync(
            Registration(Guid.NewGuid()),
            TimeSpan.FromSeconds(1),
            Deadline(),
            CancellationToken.None);
        Check.True(
            replacement.Succeeded,
            "new worker boot publishes the same static route");
        var oldBoot = await gatewayB.CommitAdmissionAsync(
            Claim(reservation.Admission!),
            Deadline(),
            CancellationToken.None);
        Check.True(
            oldBoot.Status ==
                SemanticGatewayAdmissionStatus.RouteRejected &&
            oldBoot.RouteRejection ==
                SemanticGatewayRouteSelectionStatus
                    .RouteIdentityMismatch,
            "new worker boot cannot inherit an old reservation proof");

        await Task.Delay(TimeSpan.FromMilliseconds(1_250));
        var staleLogin = await StartActivatedAsync(
            gatewayB,
            new SemanticGatewayPrincipal(702, "STALE_WORKER"),
            "192.0.2.81");
        var stale = await gatewayA.ReserveAdmissionAsync(
            staleLogin.GenerationId,
            staleLogin.Principal,
            Source("192.0.2.82"),
            Target(),
            Deadline(),
            CancellationToken.None);
        Check.True(
            stale.Status ==
                SemanticGatewayAdmissionStatus.RouteRejected &&
            stale.RouteRejection is
                SemanticGatewayRouteSelectionStatus.WorkerNotFound or
                SemanticGatewayRouteSelectionStatus.WorkerUnavailable,
            "expired worker heartbeat fails closed");
        _ = await gatewayA.CancelLoginAsync(
            staleLogin,
            Deadline(),
            CancellationToken.None);
        _ = await gatewayB.CancelLoginAsync(
            drainingLogin,
            Deadline(),
            CancellationToken.None);
    }
}
