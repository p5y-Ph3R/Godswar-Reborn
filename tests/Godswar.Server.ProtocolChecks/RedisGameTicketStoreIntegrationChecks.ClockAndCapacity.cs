using System.Security.Cryptography;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Infrastructure.Redis;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisGameTicketStoreIntegrationChecks
{
    private static async Task CheckRedisClockAuthorityAsync(
        CoordinationRuntimeOptions options,
        RedisCoordinationExecutor normalExecutor,
        RedisCoordinationKeyBuilder keys,
        HashSet<string> cleanup)
    {
        var aheadClock =
            new OffsetTimeProvider(TimeSpan.FromDays(365));
        var behindClock =
            new OffsetTimeProvider(TimeSpan.FromDays(-365));
        await using var aheadExecutor =
            await RedisCoordinationExecutor.ConnectAsync(
                options,
                "b17-ticket-clock-ahead",
                timeProvider: aheadClock);
        await using var behindExecutor =
            await RedisCoordinationExecutor.ConnectAsync(
                options,
                "b17-ticket-clock-behind",
                timeProvider: behindClock);
        await using var normal = new RedisGameTicketStore(
            normalExecutor,
            keys,
            capacity: 2,
            timeProvider: TimeProvider.System);
        await using var ahead = new RedisGameTicketStore(
            aheadExecutor,
            keys,
            capacity: 2,
            timeProvider: aheadClock);
        await using var behind = new RedisGameTicketStore(
            behindExecutor,
            keys,
            capacity: 2,
            timeProvider: behindClock);

        var baseline = await StartAsync(
            normal,
            700_010,
            "clock_normal",
            cleanup,
            keys);
        await using var baselineLease =
            await IssueAsync(normal, baseline, cleanup, keys);
        Check.True(
            await baselineLease.CommitAsync(Deadline),
            "baseline ticket activates before a skewed host participates");
        using var baselineSecrets = CopySecrets(baselineLease.Grant);

        var aheadGeneration = await StartAsync(
            ahead,
            700_011,
            "clock_ahead",
            cleanup,
            keys);
        cleanup.Add(keys.LoginAccount(700_012));
        var rejected = await behind.BeginLoginAsync(
            700_012,
            "clock_behind",
            Deadline);
        Check.Equal(
            (int)SecureLoginGenerationStatus.CapacityExceeded,
            (int)rejected.Status,
            "host clock skew cannot purge another authority's capacity");

        using var baselineBind = baselineSecrets.CreateBind();
        var consumed = await behind.ConsumeAsync(
            baselineBind,
            CreateContext(SecureEndpointRole.Game),
            Target,
            Deadline);
        Check.True(
            consumed.IsAccepted,
            "host clock skew cannot invalidate another authority's ticket");
        await ahead.RevokeGenerationAsync(aheadGeneration, Deadline);
    }

    private static async Task CheckStalePointerCapacityAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        HashSet<string> cleanup)
    {
        await using var store =
            new RedisGameTicketStore(executor, keys, capacity: 3);
        var first = await StartAsync(
            store,
            700_013,
            "stale_first",
            cleanup,
            keys);
        var second = await StartAsync(
            store,
            700_014,
            "stale_second",
            cleanup,
            keys);
        var third = await StartAsync(
            store,
            700_015,
            "stale_third",
            cleanup,
            keys);
        await using var firstLease =
            await IssueAsync(store, first, cleanup, keys);
        await using var secondLease =
            await IssueAsync(store, second, cleanup, keys);
        await using var thirdLease =
            await IssueAsync(store, third, cleanup, keys);
        using var firstSecrets = CopySecrets(firstLease.Grant);
        var firstHash = SHA256.HashData(firstSecrets.Ticket);
        var sentinelHash = SHA256.HashData(
            "b17-stale-capacity-sentinel"u8);
        try
        {
            var firstTicketKey = keys.Ticket(firstHash);
            var sentinelKey = keys.Ticket(sentinelHash);
            await executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Ticket,
                CoordinationDeadline.FromNow(TimeSpan.FromSeconds(2)),
                async database =>
                {
                    await database.SortedSetRemoveAsync(
                        keys.OutstandingTicketRegistry(),
                        firstTicketKey);
                    await database.SortedSetAddAsync(
                        keys.OutstandingTicketRegistry(),
                        sentinelKey,
                        DateTimeOffset.UtcNow
                            .AddMinutes(5)
                            .ToUnixTimeMilliseconds());
                    return true;
                });

            var replacement = await store.IssueAsync(
                first,
                CreateContext(SecureEndpointRole.Login),
                Target,
                Deadline);
            Check.Equal(
                (int)SecureTicketIssueStatus.CapacityExceeded,
                (int)replacement.Status,
                "stale account pointer cannot bypass ticket capacity");
            var count = await executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Ticket,
                CoordinationDeadline.FromNow(TimeSpan.FromSeconds(2)),
                database => database.SortedSetLengthAsync(
                    keys.OutstandingTicketRegistry()));
            Check.Equal(
                3L,
                count,
                "stale-pointer rejection preserves the global capacity cap");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstHash);
            CryptographicOperations.ZeroMemory(sentinelHash);
        }

        await store.RevokeGenerationAsync(first, Deadline);
        await store.RevokeGenerationAsync(second, Deadline);
        await store.RevokeGenerationAsync(third, Deadline);
    }

    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            TimeProvider.System.GetUtcNow() + offset;

        public override long GetTimestamp() =>
            TimeProvider.System.GetTimestamp();

        public override long TimestampFrequency =>
            TimeProvider.System.TimestampFrequency;
    }
}
