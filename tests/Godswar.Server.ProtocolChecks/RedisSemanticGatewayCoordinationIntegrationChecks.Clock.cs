using Godswar.Server.Application.Coordination;
using Godswar.Server.Infrastructure.Redis;
using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisSemanticGatewayCoordinationIntegrationChecks
{
    private static async Task CheckRedisClockAuthorityAsync(
        string connectionString)
    {
        var aheadClock =
            new SemanticOffsetTimeProvider(TimeSpan.FromDays(365));
        var behindClock =
            new SemanticOffsetTimeProvider(TimeSpan.FromDays(-365));
        var environment =
            "sg-clock-" + Guid.NewGuid().ToString("N")[..12];
        var options = CreateOptions(environment, connectionString);
        await using var aheadExecutor =
            await RedisCoordinationExecutor.ConnectAsync(
                options,
                "semantic-clock-ahead",
                timeProvider: aheadClock);
        await using var behindExecutor =
            await RedisCoordinationExecutor.ConnectAsync(
                options,
                "semantic-clock-behind",
                timeProvider: behindClock);
        var keys = new RedisCoordinationKeyBuilder(environment);
        await using var worker = new RedisWorkerCoordination(
            aheadExecutor,
            keys,
            capacity: 1,
            maximumConcurrency:
                options.MaximumConcurrentOperations);
        var registration = await worker.RegisterWorkerAsync(
            Registration(Guid.NewGuid()),
            TimeSpan.FromSeconds(10),
            SkewDeadline(aheadClock),
            CancellationToken.None);
        Check.True(
            registration.Succeeded &&
            registration.Lease is { } workerLease &&
            workerLease.ProvenUntilUtc >
                TimeProvider.System.GetUtcNow() +
                TimeSpan.FromSeconds(7) &&
            workerLease.ProvenUntilUtc <
                TimeProvider.System.GetUtcNow() +
                TimeSpan.FromSeconds(12),
            "worker expiry is derived from Redis rather than a skewed host");

        var limits = new SemanticGatewayAuthorityLimits(
            maximumLoginGenerations: 1,
            maximumAdmissions: 1,
            maximumAdmissionsPerGeneration: 1,
            loginGenerationTtl: TimeSpan.FromSeconds(2),
            reservationTtl: TimeSpan.FromSeconds(1),
            committedAdmissionTtl: TimeSpan.FromSeconds(1));
        await using var ahead = new RedisSemanticGatewayCoordination(
            aheadExecutor,
            keys,
            Directory(),
            limits,
            aheadClock);
        await using var behind = new RedisSemanticGatewayCoordination(
            behindExecutor,
            keys,
            Directory(),
            limits,
            behindClock);

        var principal =
            new SemanticGatewayPrincipal(711, "CLOCK_AUTHORITY");
        var login = await ahead.StartLoginAsync(
            principal,
            Source("192.0.2.111"),
            SemanticGatewayTestRealm.TempestGrant,
            SkewDeadline(aheadClock),
            CancellationToken.None);
        Check.True(
            login.IsStarted &&
            login.Generation!.ExpiresAt >
                TimeProvider.System.GetUtcNow() +
                TimeSpan.FromSeconds(1) &&
            login.Generation.ExpiresAt <
                TimeProvider.System.GetUtcNow() +
                TimeSpan.FromSeconds(4),
            "login expiry is derived from Redis rather than a skewed host");
        var generation =
            login.Generation ??
            throw new InvalidOperationException(
                "Clock authority login returned no generation.");
        Check.True(
            await behind.ActivateLoginAsync(
                generation,
                SkewDeadline(behindClock),
                CancellationToken.None),
            "oppositely skewed gateway activates the live generation");

        _ = await ahead.SweepExpiredAsync(
            SkewDeadline(aheadClock),
            CancellationToken.None);
        var blocked = await behind.StartLoginAsync(
            new SemanticGatewayPrincipal(712, "CLOCK_CAPACITY"),
            Source("192.0.2.112"),
            SemanticGatewayTestRealm.TempestGrant,
            SkewDeadline(behindClock),
            CancellationToken.None);
        Check.True(
            blocked.Status == SemanticGatewayLoginStatus.CapacityExceeded,
            "skewed sweeper cannot purge live login capacity");

        var reserved = await behind.ReserveAdmissionAsync(
            generation.GenerationId,
            principal,
            Source("192.0.2.113"),
            Target(),
            SkewDeadline(behindClock),
            CancellationToken.None);
        var claim = Claim(
            reserved.Admission ??
            throw new InvalidOperationException(
                "Clock authority reservation returned no admission."));
        var committed = await ahead.CommitAdmissionAsync(
            claim,
            SkewDeadline(aheadClock),
            CancellationToken.None);
        var refreshed = await behind.RefreshAdmissionAsync(
            claim,
            SkewDeadline(behindClock),
            CancellationToken.None);
        var refreshedLogin = await ahead.FindActivatedLoginAsync(
            principal.CanonicalUsername!,
            generation.LoginSource.Address!,
            SkewDeadline(aheadClock),
            CancellationToken.None);
        Check.True(
            reserved.Status == SemanticGatewayAdmissionStatus.Reserved &&
            committed.Status == SemanticGatewayAdmissionStatus.Committed &&
            refreshed.Status == SemanticGatewayAdmissionStatus.Refreshed &&
            refreshed.Admission is { } admission &&
            refreshedLogin.IsFound &&
            refreshedLogin.Generation!.ExpiresAt >=
                admission.ExpiresAt + TimeSpan.FromMilliseconds(500),
            "refresh returns distinct Redis-derived admission and login " +
            "expiries across skewed gateways");

        _ = await ahead.SweepExpiredAsync(
            SkewDeadline(aheadClock),
            CancellationToken.None);
        Check.True(
            (await behind.ResolveAdmissionAsync(
                claim,
                SkewDeadline(behindClock),
                CancellationToken.None)).Status ==
                SemanticGatewayAdmissionStatus.Committed,
            "extreme host skew cannot expire a live admission");

        await Task.Delay(TimeSpan.FromMilliseconds(2_300));
        _ = await behind.SweepExpiredAsync(
            SkewDeadline(behindClock),
            CancellationToken.None);
        var expiredLogin = await behind.FindActivatedLoginAsync(
            principal.CanonicalUsername!,
            generation.LoginSource.Address!,
            SkewDeadline(behindClock),
            CancellationToken.None);
        Check.True(
            expiredLogin.Status is
                SemanticGatewayLoginLookupStatus.NotFound or
                SemanticGatewayLoginLookupStatus.Expired,
            "behind host cannot preserve a Redis-expired generation");

        var replacement = await ahead.StartLoginAsync(
            new SemanticGatewayPrincipal(713, "CLOCK_REUSED"),
            Source("192.0.2.114"),
            SemanticGatewayTestRealm.TempestGrant,
            SkewDeadline(aheadClock),
            CancellationToken.None);
        Check.True(
            replacement.IsStarted &&
            await behind.ActivateLoginAsync(
                replacement.Generation!,
                SkewDeadline(behindClock),
                CancellationToken.None),
            "Redis expiry makes generation capacity reusable across hosts");
        var reused = await ahead.ReserveAdmissionAsync(
            replacement.Generation!.GenerationId,
            replacement.Generation.Principal,
            Source("192.0.2.115"),
            Target(),
            SkewDeadline(aheadClock),
            CancellationToken.None);
        Check.True(
            reused.Status == SemanticGatewayAdmissionStatus.Reserved,
            "Redis expiry makes admission capacity reusable across hosts");
        _ = await behind.RollbackAdmissionAsync(
            Claim(reused.Admission!),
            SkewDeadline(behindClock),
            CancellationToken.None);
        _ = await ahead.CancelLoginAsync(
            replacement.Generation!,
            SkewDeadline(aheadClock),
            CancellationToken.None);
        _ = await worker.ReleaseWorkerAsync(
            registration.Lease!.Value,
            SkewDeadline(aheadClock),
            CancellationToken.None);
        await aheadExecutor.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            SkewDeadline(aheadClock),
            database => database.KeyDeleteAsync(
                [
                    keys.GatewayCounters(),
                    keys.GatewayExpiry()
                ]));
    }

    private static CoordinationDeadline SkewDeadline(
        TimeProvider timeProvider) =>
        CoordinationDeadline.FromNow(
            TimeSpan.FromSeconds(4),
            timeProvider);

    private sealed class SemanticOffsetTimeProvider(TimeSpan offset) :
        TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            TimeProvider.System.GetUtcNow() + offset;

        public override long GetTimestamp() =>
            TimeProvider.System.GetTimestamp();

        public override long TimestampFrequency =>
            TimeProvider.System.TimestampFrequency;
    }
}
