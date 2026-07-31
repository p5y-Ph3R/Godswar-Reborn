using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulProtocolChecks
{
    private static void CheckWorkerAdmissionChurnAndReplayBounds()
    {
        const int liveCapacity = 2;
        const int replayCapacity = 3;
        var clock = new ManualTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(100));
        using var registry = Registry(
            clock,
            liveCapacity,
            replayCapacity);

        GatewayWorldAdmission? latest = null;
        for (var index = 0; index < 20; index++)
        {
            latest = Admission(
                connectionId: GuidFromInt(10_000 + index),
                issued: clock.GetUtcNow(),
                expires: clock.GetUtcNow().AddSeconds(30));
            Check.True(
                registry.TryReserve(latest, out var lease) ==
                    BackhaulAdmissionStatus.Accepted &&
                lease is not null &&
                lease.Activate(),
                $"churn connection {index} acquires live capacity");
            lease!.Dispose();

            var snapshot = registry.GetSnapshot();
            Check.True(
                snapshot.ReservedAdmissions == 0 &&
                snapshot.ActiveAdmissions == 0 &&
                snapshot.ActiveAccounts == 0,
                $"churn connection {index} releases all live capacity");
            AssertAdmissionCollectionsBounded(
                snapshot,
                liveCapacity,
                replayCapacity,
                $"churn connection {index}");
        }

        var afterChurn = registry.GetSnapshot();
        Check.True(
            afterChurn.ReplayTombstones == replayCapacity &&
            afterChurn.ReplayEvictions == 17,
            "replay budget deterministically evicts its oldest entries");
        Check.True(
            registry.TryReserve(latest!, out _) ==
                BackhaulAdmissionStatus.ReplayRejected,
            "the newest retained tombstone rejects an exact replay");

        var first = Admission(
            connectionId: GuidFromInt(20_001),
            accountId: 101,
            username: "LIVE101",
            issued: clock.GetUtcNow(),
            expires: clock.GetUtcNow().AddSeconds(30));
        var second = Admission(
            connectionId: GuidFromInt(20_002),
            accountId: 102,
            username: "LIVE102",
            issued: clock.GetUtcNow(),
            expires: clock.GetUtcNow().AddSeconds(30));
        var overflow = Admission(
            connectionId: GuidFromInt(20_003),
            accountId: 103,
            username: "LIVE103",
            issued: clock.GetUtcNow(),
            expires: clock.GetUtcNow().AddSeconds(30));
        Check.True(
            registry.TryReserve(first, out var firstLease) ==
                BackhaulAdmissionStatus.Accepted,
            "first live slot remains usable after churn");
        Check.True(
            registry.TryReserve(second, out var secondLease) ==
                BackhaulAdmissionStatus.Accepted,
            "second live slot remains usable after churn");
        Check.True(
            registry.TryReserve(overflow, out _) ==
                BackhaulAdmissionStatus.CapacityExceeded,
            "live capacity remains fail-closed at its own bound");
        AssertAdmissionCollectionsBounded(
            registry.GetSnapshot(),
            liveCapacity,
            replayCapacity,
            "full live capacity");

        firstLease!.Dispose();
        Check.True(
            registry.TryReserve(overflow, out var replacementLease) ==
                BackhaulAdmissionStatus.Accepted,
            "a release immediately frees one live slot despite tombstones");
        replacementLease!.Dispose();
        secondLease!.Dispose();

        clock.Advance(TimeSpan.FromSeconds(36));
        var expired = registry.GetSnapshot();
        Check.True(
            expired.TrackedAdmissions == 0 &&
            expired.ReplayTombstones == 0 &&
            expired.ReservedExpiryMarkers == 0 &&
            expired.ReplayExpiryMarkers == 0,
            "bounded cleanup expires every live and replay index");
    }

    private static void AssertAdmissionCollectionsBounded(
        WorkerBackhaulAdmissionRegistrySnapshot snapshot,
        int liveCapacity,
        int replayCapacity,
        string scope)
    {
        var live =
            snapshot.ReservedAdmissions + snapshot.ActiveAdmissions;
        Check.True(
            snapshot.Capacity == liveCapacity &&
            snapshot.ReplayCapacity == replayCapacity &&
            live <= liveCapacity &&
            snapshot.ReplayTombstones <= replayCapacity &&
            snapshot.TrackedAdmissions <=
                liveCapacity + replayCapacity,
            $"{scope} keeps admission dictionaries bounded");
        Check.True(
            snapshot.ReservedExpiryMarkers <=
                snapshot.ReservedAdmissions &&
            snapshot.ReplayExpiryMarkers ==
                snapshot.ReplayTombstones &&
            snapshot.ReservedExpiryMarkers +
                snapshot.ReplayExpiryMarkers <=
                    liveCapacity + replayCapacity,
            $"{scope} keeps expiry indexes bounded");
    }
}
