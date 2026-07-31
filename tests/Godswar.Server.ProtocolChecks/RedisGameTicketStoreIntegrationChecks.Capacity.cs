using Godswar.Server.Infrastructure.Redis;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisGameTicketStoreIntegrationChecks
{
    private static async Task CheckCapacityAndCachedSnapshotAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        HashSet<string> cleanup)
    {
        await using var store =
            new RedisGameTicketStore(executor, keys, capacity: 2);
        var first =
            await StartAsync(store, 700_007, "redis_seven", cleanup, keys);
        var second =
            await StartAsync(store, 700_008, "redis_eight", cleanup, keys);
        cleanup.Add(keys.LoginAccount(700_009));
        var rejected = await store.BeginLoginAsync(
            700_009,
            "redis_nine",
            Deadline);
        Check.Equal(
            (int)SecureLoginGenerationStatus.CapacityExceeded,
            (int)rejected.Status,
            "Redis generation capacity is atomic");
        Check.Equal(
            2,
            store.GetCachedSnapshot().ActiveGenerations,
            "cached Redis generation count is bounded");

        await using var firstLease =
            await IssueAsync(store, first, cleanup, keys);
        await using var secondLease =
            await IssueAsync(store, second, cleanup, keys);
        var snapshot = store.GetCachedSnapshot();
        Check.Equal(
            2,
            snapshot.OutstandingTickets,
            "cached Redis outstanding-ticket count is bounded");
        await store.RevokeGenerationAsync(first, Deadline);
        await store.RevokeGenerationAsync(second, Deadline);
        snapshot = store.GetCachedSnapshot();
        Check.Equal(
            0,
            snapshot.ActiveGenerations,
            "Redis revocation refreshes cached generation count");
        Check.Equal(
            0,
            snapshot.OutstandingTickets,
            "Redis revocation refreshes cached ticket count");
    }
}
