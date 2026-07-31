using System.Security.Cryptography;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Infrastructure.Redis;
using Godswar.Server.Networking.Secure;
using StackExchange.Redis;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisGameTicketStoreIntegrationChecks
{
    public const string CheckName =
        "Redis atomic secure game-ticket authority";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_REDIS_CONNECTION_STRING";

    private static readonly SecureGameTarget Target = new(
        "127.1.1.110",
        "game.reborn.test",
        "reborn-game",
        routePort: 7000,
        tlsPort: 7443,
        serverId: 100);

    private static SecureTicketOperationDeadline Deadline =>
        new(TimeSpan.FromSeconds(2));

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        var environment =
            $"ticket_test_{Guid.NewGuid():N}"[..29];
        var options = CreateOptions(
            connectionString,
            environment);
        await using var executor =
            await RedisCoordinationExecutor.ConnectAsync(
                options,
                "b17-ticket-test");
        var keys = new RedisCoordinationKeyBuilder(environment);
        var cleanup = new HashSet<string>(StringComparer.Ordinal)
        {
            keys.TicketGenerationRegistry(),
            keys.OutstandingTicketRegistry()
        };

        try
        {
            await CheckCrossAuthorityAndHashOnlyAsync(
                executor,
                keys,
                cleanup);
            await CheckPendingReplacementAndScopeAsync(
                executor,
                keys,
                cleanup);
            await CheckForgeryAndUnknownGrantAsync(
                executor,
                keys,
                cleanup);
            await CheckConcurrentConsumeAndRevocationAsync(
                executor,
                keys,
                cleanup);
            await CheckCapacityAndCachedSnapshotAsync(
                executor,
                keys,
                cleanup);
            await CheckRedisClockAuthorityAsync(
                options,
                executor,
                keys,
                cleanup);
            await CheckStalePointerCapacityAsync(
                executor,
                keys,
                cleanup);
            await CheckLogicalExpiryAsync(
                executor,
                keys,
                cleanup);
        }
        finally
        {
            await CleanupAsync(executor, cleanup);
        }
    }

    private static async Task CheckCrossAuthorityAndHashOnlyAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        HashSet<string> cleanup)
    {
        await using var issuer =
            new RedisGameTicketStore(executor, keys, capacity: 64);
        await using var consumer =
            new RedisGameTicketStore(executor, keys, capacity: 64);
        var generation =
            await StartAsync(issuer, 700_001, "redis_one", cleanup, keys);
        await using var lease =
            await IssueAsync(issuer, generation, cleanup, keys);
        using var secrets = CopySecrets(lease.Grant);

        var hash = SHA256.HashData(secrets.Ticket);
        try
        {
            var ticketKey = keys.Ticket(hash);
            var grantId = new Guid(secrets.GrantId);
            var grantKey = keys.TicketGrant(grantId);
            Check.True(
                !ticketKey.Contains(
                    Convert.ToHexString(secrets.Ticket),
                    StringComparison.OrdinalIgnoreCase),
                "Redis ticket key excludes the raw ticket");
            Check.True(
                !grantKey.Contains(
                    grantId.ToString("N"),
                    StringComparison.OrdinalIgnoreCase),
                "Redis grant key excludes the raw grant ID");

            var entries = await executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Ticket,
                CoordinationDeadline.FromNow(TimeSpan.FromSeconds(2)),
                database => database.HashGetAllAsync(ticketKey));
            var serialized = string.Join(
                "|",
                entries.Select(entry =>
                    $"{entry.Name}={entry.Value}"));
            Check.True(
                !serialized.Contains(
                    Convert.ToHexString(secrets.Ticket),
                    StringComparison.OrdinalIgnoreCase) &&
                !serialized.Contains(
                    Convert.ToBase64String(secrets.Ticket),
                    StringComparison.Ordinal),
                "Redis stores only the ticket digest");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }

        Check.True(
            await lease.CommitAsync(Deadline),
            "Redis ticket activates after grant delivery");
        using var bind = secrets.CreateBind();
        var accepted = await consumer.ConsumeAsync(
            bind,
            CreateContext(SecureEndpointRole.Game),
            Target,
            Deadline);
        Check.True(
            accepted.IsAccepted &&
            accepted.Principal?.AccountId == 700_001,
            "another authority process consumes the Redis ticket");
        using var replay = secrets.CreateBind();
        Check.Equal(
            (int)SecureTicketConsumeStatus.Rejected,
            (int)(await consumer.ConsumeAsync(
                replay,
                CreateContext(SecureEndpointRole.Game),
                Target,
                Deadline)).Status,
            "Redis ticket is consume-once");
    }

    private static async Task CheckPendingReplacementAndScopeAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        HashSet<string> cleanup)
    {
        await using var first =
            new RedisGameTicketStore(executor, keys, capacity: 64);
        await using var replacement =
            new RedisGameTicketStore(executor, keys, capacity: 64);

        var pendingGeneration =
            await StartAsync(first, 700_002, "redis_two", cleanup, keys);
        await using var pending =
            await IssueAsync(first, pendingGeneration, cleanup, keys);
        using var pendingSecrets = CopySecrets(pending.Grant);
        using (var bind = pendingSecrets.CreateBind())
        {
            Check.Equal(
                (int)SecureTicketConsumeStatus.NotReady,
                (int)(await replacement.ConsumeAsync(
                    bind,
                    CreateContext(SecureEndpointRole.Game),
                    Target,
                    Deadline)).Status,
                "pending Redis ticket cannot be consumed");
        }
        Check.True(
            await pending.CommitAsync(Deadline),
            "pending Redis ticket remains available for redirect commit");
        using (var committedBind = pendingSecrets.CreateBind())
        {
            Check.True(
                (await replacement.ConsumeAsync(
                    committedBind,
                    CreateContext(SecureEndpointRole.Game),
                    Target,
                    Deadline)).IsAccepted,
                "post-redirect Redis commit activates the pending ticket");
        }

        var oldGeneration =
            await StartAsync(first, 700_003, "redis_three", cleanup, keys);
        await using var oldLease =
            await IssueAsync(first, oldGeneration, cleanup, keys);
        Check.True(
            await oldLease.CommitAsync(Deadline),
            "old generation ticket activates");
        using var oldSecrets = CopySecrets(oldLease.Grant);

        var currentGeneration = await StartAsync(
            replacement,
            700_003,
            "redis_three",
            cleanup,
            keys);
        using (var oldBind = oldSecrets.CreateBind())
        {
            Check.Equal(
                (int)SecureTicketConsumeStatus.Rejected,
                (int)(await replacement.ConsumeAsync(
                    oldBind,
                    CreateContext(SecureEndpointRole.Game),
                    Target,
                    Deadline)).Status,
                "new generation atomically invalidates its predecessor");
        }

        await using var currentLease =
            await IssueAsync(
                replacement,
                currentGeneration,
                cleanup,
                keys);
        Check.True(
            await currentLease.CommitAsync(Deadline),
            "replacement generation ticket activates");
        using var currentSecrets = CopySecrets(currentLease.Grant);
        using var wrongScope = currentSecrets.CreateBind();
        var scopeResult = await replacement.ConsumeAsync(
            wrongScope,
            CreateContext(
                SecureEndpointRole.Game,
                instanceSeed: 0x61),
            Target,
            Deadline);
        Check.Equal(
            (int)SecureTicketConsumeStatus.ScopeRejected,
            (int)scopeResult.Status,
            "Redis consume validates the complete client scope");
        using var burned = currentSecrets.CreateBind();
        Check.Equal(
            (int)SecureTicketConsumeStatus.Rejected,
            (int)(await replacement.ConsumeAsync(
                burned,
                CreateContext(SecureEndpointRole.Game),
                Target,
                Deadline)).Status,
            "scope failure burns the Redis ticket");
    }

    private static async Task CheckConcurrentConsumeAndRevocationAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        HashSet<string> cleanup)
    {
        await using var store =
            new RedisGameTicketStore(executor, keys, capacity: 64);
        var generation =
            await StartAsync(store, 700_004, "redis_four", cleanup, keys);
        await using var lease =
            await IssueAsync(store, generation, cleanup, keys);
        Check.True(
            await lease.CommitAsync(Deadline),
            "concurrent Redis ticket activates");
        using var secrets = CopySecrets(lease.Grant);

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                using var bind = secrets.CreateBind();
                return await store.ConsumeAsync(
                    bind,
                    CreateContext(SecureEndpointRole.Game),
                    Target,
                    Deadline);
            }))
            .ToArray();
        var results = await Task.WhenAll(attempts);
        Check.Equal(
            1,
            results.Count(result => result.IsAccepted),
            "Redis Lua consume has exactly one concurrent winner");

        var revokedGeneration =
            await StartAsync(store, 700_005, "redis_five", cleanup, keys);
        await using var revokedLease =
            await IssueAsync(store, revokedGeneration, cleanup, keys);
        using var revokedSecrets = CopySecrets(revokedLease.Grant);
        await store.RevokeGenerationAsync(
            revokedGeneration,
            Deadline);
        Check.True(
            !await revokedLease.CommitAsync(Deadline),
            "generation revocation removes its Redis grant index");
        using var revokedBind = revokedSecrets.CreateBind();
        Check.Equal(
            (int)SecureTicketConsumeStatus.Rejected,
            (int)(await store.ConsumeAsync(
                revokedBind,
                CreateContext(SecureEndpointRole.Game),
                Target,
                Deadline)).Status,
            "revoked Redis generation cannot be consumed");
    }

    private static async Task CheckLogicalExpiryAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        HashSet<string> cleanup)
    {
        await using var store = new RedisGameTicketStore(
            executor,
            keys,
            capacity: 64,
            ticketTtl: TimeSpan.FromSeconds(1));
        var generation =
            await StartAsync(store, 700_006, "redis_six", cleanup, keys);
        await using var lease =
            await IssueAsync(store, generation, cleanup, keys);
        Check.True(
            await lease.CommitAsync(Deadline),
            "expiring Redis ticket activates");
        using var secrets = CopySecrets(lease.Grant);
        await Task.Delay(TimeSpan.FromMilliseconds(1_100));
        using var bind = secrets.CreateBind();
        Check.Equal(
            (int)SecureTicketConsumeStatus.Expired,
            (int)(await store.ConsumeAsync(
                bind,
                CreateContext(SecureEndpointRole.Game),
                Target,
                Deadline)).Status,
            "Redis ticket is unusable at its logical TTL");
    }

    private static async ValueTask<SecureLoginGeneration> StartAsync(
        RedisGameTicketStore store,
        int accountId,
        string username,
        HashSet<string> cleanup,
        RedisCoordinationKeyBuilder keys)
    {
        cleanup.Add(keys.LoginAccount(accountId));
        var result = await store.BeginLoginAsync(
            accountId,
            username,
            Deadline);
        Check.True(result.IsStarted, "Redis login generation starts");
        return result.Generation!;
    }

    private static async ValueTask<SecureGameGrantLease> IssueAsync(
        RedisGameTicketStore store,
        SecureLoginGeneration generation,
        HashSet<string> cleanup,
        RedisCoordinationKeyBuilder keys)
    {
        var result = await store.IssueAsync(
            generation,
            CreateContext(SecureEndpointRole.Login),
            Target,
            Deadline);
        Check.True(result.IsIssued, "Redis game ticket issues");
        var lease = result.Lease!;
        using var secrets = CopySecrets(lease.Grant);
        var hash = SHA256.HashData(secrets.Ticket);
        try
        {
            cleanup.Add(keys.Ticket(hash));
            cleanup.Add(
                keys.TicketGrant(new Guid(secrets.GrantId)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
        return lease;
    }

    private static GrantSecrets CopySecrets(SecureGameGrant grant)
    {
        var grantId =
            new byte[SecureProtocolConstants.GrantIdBytes];
        var ticket =
            new byte[SecureProtocolConstants.TicketBytes];
        Check.True(
            grant.TryCopySecrets(grantId, ticket),
            "Redis grant secrets are available before disposal");
        return new GrantSecrets(grantId, ticket);
    }

    private static SecureConnectionContext CreateContext(
        SecureEndpointRole role,
        byte instanceSeed = 0x11)
    {
        var connectionId =
            Enumerable.Repeat(instanceSeed, 16).ToArray();
        var clientInstance =
            Enumerable.Repeat(instanceSeed, 16).ToArray();
        var origin = Enumerable.Repeat((byte)0x31, 32).ToArray();
        return new SecureConnectionContext(
            role,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            connectionId,
            clientInstance,
            origin);
    }

    private static CoordinationRuntimeOptions CreateOptions(
        string connectionString,
        string environment)
    {
        var parsed = ConfigurationOptions.Parse(connectionString);
        var options = new CoordinationRuntimeOptions
        {
            Provider = "Redis",
            Environment = environment,
            ConnectionStringEnvironmentVariable =
                ConnectionStringVariable,
            Capacity = 512,
            MaximumConcurrentOperations = 64,
            QueueAdmissionTimeoutMilliseconds = 500,
            OperationTimeoutMilliseconds = 2_000,
            ConnectTimeoutMilliseconds = 3_000,
            CircuitFailureThreshold = 5,
            CircuitOpenMilliseconds = 1_000,
            RequireTls = parsed.Ssl
        };
        options.NormalizeAndValidate();
        return options;
    }

    private static async Task CleanupAsync(
        RedisCoordinationExecutor executor,
        HashSet<string> cleanup)
    {
        try
        {
            var keys = cleanup
                .Select(key => (RedisKey)key)
                .ToArray();
            if (keys.Length == 0)
            {
                return;
            }
            await executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Ticket,
                CoordinationDeadline.FromNow(TimeSpan.FromSeconds(2)),
                database => database.KeyDeleteAsync(keys));
        }
        catch
        {
        }
    }

    private sealed class GrantSecrets(
        byte[] grantId,
        byte[] ticket) : IDisposable
    {
        public byte[] GrantId { get; } = grantId;

        public byte[] Ticket { get; } = ticket;

        public SecureGameBind CreateBind() =>
            new(GrantId, Ticket);

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(GrantId);
            CryptographicOperations.ZeroMemory(Ticket);
        }
    }
}
