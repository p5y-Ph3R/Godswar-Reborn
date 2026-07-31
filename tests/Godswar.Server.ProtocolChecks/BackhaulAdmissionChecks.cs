using System.Net;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulProtocolChecks
{
    private static void CheckWorkerAdmissionLifecycle()
    {
        var clock = new ManualTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(100));
        using var registry = Registry(clock, capacity: 3);
        var admission = Admission(
            issued: clock.GetUtcNow(),
            expires: clock.GetUtcNow().AddSeconds(30));

        var status = registry.TryReserve(
            admission,
            out var lease);
        Check.True(
            status == BackhaulAdmissionStatus.Accepted &&
            lease is not null,
            "worker reserves an exact authenticated admission");
        var reserved = registry.GetSnapshot();
        Check.True(
            reserved.TrackedAdmissions == 1 &&
            reserved.ReservedAdmissions == 1 &&
            reserved.ActiveAccounts == 1,
            "reservation accounts for one bounded account and connection");
        Check.True(
            lease!.Activate() &&
            lease.Activate() &&
            lease.IsActive,
            "activation is successful and idempotent");
        var active = registry.GetSnapshot();
        Check.True(
            active.ActiveAdmissions == 1 &&
            active.ReservedAdmissions == 0,
            "activated admission changes state exactly once");

        Check.True(
            registry.TryReserve(admission, out _) ==
                BackhaulAdmissionStatus.ReplayRejected,
            "same connection ID is replay-rejected");
        Check.True(
            registry.TryReserve(
                Admission(
                    connectionId: Guid.NewGuid(),
                    accountId: admission.AccountId,
                    issued: clock.GetUtcNow(),
                    expires: clock.GetUtcNow().AddSeconds(30)),
                out _) ==
                BackhaulAdmissionStatus.AccountAlreadyActive,
            "a second connection cannot own the same account");

        lease.Dispose();
        lease.Dispose();
        var released = registry.GetSnapshot();
        Check.True(
            released.ReplayTombstones == 1 &&
            released.ActiveAccounts == 0 &&
            released.ActiveAdmissions == 0,
            "release is idempotent and retains only a replay tombstone");
        Check.True(
            registry.TryReserve(admission, out _) ==
                BackhaulAdmissionStatus.ReplayRejected,
            "closed connection remains replay-rejected");

        var replacement = Admission(
            connectionId: Guid.NewGuid(),
            issued: clock.GetUtcNow(),
            expires: clock.GetUtcNow().AddSeconds(30));
        Check.True(
            registry.TryReserve(replacement, out var replacementLease) ==
                BackhaulAdmissionStatus.Accepted,
            "released account can acquire a new connection generation");
        replacementLease!.Dispose();
    }

    private static void CheckWorkerAdmissionPolicyAndExpiry()
    {
        var clock = new ManualTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(100));
        using var policy = Registry(clock, capacity: 4);
        var now = clock.GetUtcNow();

        Check.True(
            policy.TryReserve(
                Admission(
                    node: new ServerNodeId("worker-b"),
                    issued: now,
                    expires: now.AddSeconds(30)),
                out _) ==
                BackhaulAdmissionStatus.RouteRejected,
            "wrong target node is rejected");
        Check.True(
            policy.TryReserve(
                Admission(
                    map: new MapId(5),
                    issued: now,
                    expires: now.AddSeconds(30)),
                out _) ==
                BackhaulAdmissionStatus.RouteRejected,
            "unowned exact map route is rejected");
        Check.True(
            policy.TryReserve(
                Admission(
                    world: WorldInstanceId.New(),
                    issued: now,
                    expires: now.AddSeconds(30)),
                out _) ==
                BackhaulAdmissionStatus.RouteRejected,
            "unowned world instance is rejected without fallback");
        var aheadAdmission = Admission(
            connectionId: Guid.NewGuid(),
            accountId: 81,
            username: "CLOCK_AHEAD",
            issued: now.AddDays(365),
            expires: now.AddDays(365).AddSeconds(30));
        Check.True(
            policy.TryReserve(
                aheadAdmission,
                out var aheadLease) ==
                BackhaulAdmissionStatus.Accepted,
            "worker admission lifetime is neutral to +365d wall skew");
        aheadLease!.Dispose();
        var behindAdmission = Admission(
            connectionId: Guid.NewGuid(),
            accountId: 82,
            username: "CLOCK_BEHIND",
            issued: now.AddDays(-365),
            expires: now.AddDays(-365).AddSeconds(30));
        Check.True(
            policy.TryReserve(
                behindAdmission,
                out var behindLease) ==
                BackhaulAdmissionStatus.Accepted,
            "worker admission lifetime is neutral to -365d wall skew");
        behindLease!.Dispose();
        var minimumLifetime = Admission(
            connectionId: Guid.NewGuid(),
            accountId: 83,
            username: "MIN_LIFETIME",
            issued: now,
            expires: now.AddSeconds(1));
        Check.True(
            policy.TryReserve(
                minimumLifetime,
                out var minimumLease) ==
                BackhaulAdmissionStatus.Accepted &&
            minimumLease!.Activate(),
            "minimum admission lifetime survives its conservative local " +
            "safety margin through activation");
        minimumLease!.Dispose();

        using var bounded = Registry(clock, capacity: 1);
        var first = Admission(
            issued: now,
            expires: now.AddSeconds(10));
        Check.True(
            bounded.TryReserve(first, out var firstLease) ==
                BackhaulAdmissionStatus.Accepted,
            "capacity-one registry admits its first reservation");
        Check.True(
            bounded.TryReserve(
                Admission(
                    connectionId: Guid.NewGuid(),
                    accountId: 8,
                    username: "OTHER",
                    issued: now,
                    expires: now.AddSeconds(10)),
                out _) ==
                BackhaulAdmissionStatus.CapacityExceeded,
            "tracked admission capacity is fail-closed");
        firstLease!.Dispose();
        clock.Advance(TimeSpan.FromSeconds(16));
        Check.True(
            bounded.GetSnapshot().TrackedAdmissions == 0,
            "expired replay tombstone is removed by bounded cleanup");

        using var draining = Registry(clock, capacity: 2);
        draining.BeginDrain();
        Check.True(
            draining.GetSnapshot().IsDraining &&
            draining.TryReserve(
                Admission(
                    issued: clock.GetUtcNow(),
                    expires: clock.GetUtcNow().AddSeconds(30)),
                out _) ==
                BackhaulAdmissionStatus.Draining,
            "draining worker rejects all new admissions");
    }

    private static WorkerBackhaulAdmissionRegistry Registry(
        TimeProvider clock,
        int capacity,
        int replayCapacity = 8) =>
        new(
            WorkerNode,
            [new BackhaulOwnedWorldRoute(Realm, Map, World)],
            capacity,
            replayCapacity,
            replayRetention: TimeSpan.FromSeconds(5),
            admissionLifetimeSafetyMargin: TimeSpan.FromSeconds(5),
            cleanupBatchSize: 64,
            timeProvider: clock);
}
