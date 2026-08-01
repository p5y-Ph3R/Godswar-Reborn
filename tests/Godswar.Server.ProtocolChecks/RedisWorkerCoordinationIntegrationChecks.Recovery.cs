using System.Diagnostics;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Coordination;
using Godswar.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisWorkerCoordinationIntegrationChecks
{
    private static async Task CheckLiveRuntimeRecoveryAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        CoordinationRuntimeOptions options,
        HashSet<string> cleanup)
    {
        var route = new CoordinatedWorldRoute(
            RealmId.Tempest,
            MapId.FromLegacy(2),
            new WorldInstanceId(Guid.NewGuid()));
        var node = new ServerNodeId("b17-live-runtime");
        const int characterId = 700_019;
        cleanup.Add(keys.Worker(node));
        cleanup.Add(keys.Route(route.WorldInstanceId));
        cleanup.Add(keys.Player(characterId));

        var runtimeOptions = new CoordinationRuntimeOptions
        {
            Provider = "Local",
            Capacity = options.Capacity,
            MaximumConcurrentOperations =
                options.MaximumConcurrentOperations,
            QueueAdmissionTimeoutMilliseconds = 100,
            OperationTimeoutMilliseconds = 500,
            ServerHeartbeatSeconds = 1,
            ServerTtlSeconds = 5,
            PlayerLeaseRenewalSeconds = 1,
            PlayerLeaseTtlSeconds = 5
        };
        var adapter = new RedisWorkerCoordination(
            executor,
            keys,
            capacity: 512,
            maximumConcurrency: 16);
        await using var runtime = new WorkerCoordinationRuntime(
            adapter,
            runtimeOptions,
            WorldOptions(node, route),
            contentRevision: "content-test",
            buildRevision: "build-recovery");
        using var stop = new CancellationTokenSource();
        var run = runtime.RunAsync(stop.Token);
        await runtime.WaitUntilRegisteredAsync().WaitAsync(
            TimeSpan.FromSeconds(3));
        var initialRoute = await adapter.FindRouteAsync(route, Deadline);
        Check.True(
            initialRoute.IsFound &&
            initialRoute.Route!.WorkerState ==
                CoordinatedWorkerState.Draining &&
            !runtime.IsReady,
            "live worker remains draining before dependency release");
        await runtime.PublishAvailableAsync();
        await runtime.WaitUntilReadyAsync().WaitAsync(
            TimeSpan.FromSeconds(3));

        var beforeRoute = await adapter.FindRouteAsync(route, Deadline);
        Check.True(
            beforeRoute.IsFound,
            "live recovery fixture publishes its exact route");
        var boot = beforeRoute.Route!.BootId;
        var ownership =
            new PlayerOwnershipFence(Guid.NewGuid(), 29);
        var ownershipLost = 0;
        await using var player = await runtime.AcquireAsync(
            accountId: 700_001,
            characterId,
            ownership,
            route,
            () => Interlocked.Increment(ref ownershipLost)) ??
            throw new InvalidOperationException(
                "Live recovery fixture could not acquire its player.");
        Check.True(
            await player.PublishOnlineAsync(route),
            "live recovery fixture publishes online presence");
        var beforePlayer =
            await adapter.FindPlayerLeaseAsync(characterId, Deadline);
        Check.True(
            beforePlayer.IsFound,
            "live recovery fixture stores its player projection");
        var token = beforePlayer.Lease!.LeaseToken;

        var deleted = await executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Health,
            Deadline,
            database => database.KeyDeleteAsync(
            [
                keys.Worker(node),
                keys.Route(route.WorldInstanceId),
                keys.Player(characterId)
            ]));
        Check.Equal(
            3L,
            deleted,
            "simulated FLUSHDB removes live worker, route, and player keys");

        await WaitUntilAsync(
            async () =>
            {
                var restoredRoute =
                    await adapter.FindRouteAsync(route, Deadline);
                var restoredPlayer =
                    await adapter.FindPlayerLeaseAsync(
                        characterId,
                        Deadline);
                return runtime.IsReady &&
                    player.IsCurrent &&
                    restoredRoute.IsFound &&
                    restoredRoute.Route!.BootId == boot &&
                    restoredPlayer.IsFound &&
                    restoredPlayer.Lease!.Ownership == ownership &&
                    restoredPlayer.Lease.LeaseToken == token &&
                    restoredPlayer.Lease.Presence ==
                        CoordinatedPresenceState.Online;
            },
            TimeSpan.FromSeconds(6));
        Check.Equal(
            0,
            ownershipLost,
            "cache loss does not invent an ownership loss");

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task CheckSlowRedisDeadlineAndRecoveryAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys)
    {
        const string adminVariable =
            "GODSWAR_TEST_REDIS_ADMIN_CONNECTION_STRING";
        var adminConnection =
            Environment.GetEnvironmentVariable(adminVariable);
        if (string.IsNullOrWhiteSpace(adminConnection))
        {
            Console.WriteLine(
                "SKIP Redis CLIENT PAUSE fault injection " +
                $"({adminVariable} is not set)");
            return;
        }

        var adminOptions = ConfigurationOptions.Parse(adminConnection);
        adminOptions.AllowAdmin = true;
        adminOptions.AbortOnConnectFail = true;
        adminOptions.ClientName = "b17-test-admin";
        adminOptions.CommandMap = CommandMap.Create(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CONFIG",
                "CLUSTER"
            },
            available: false);
        adminOptions.TieBreaker = string.Empty;
        await using var admin =
            await ConnectionMultiplexer.ConnectAsync(adminOptions);
        var endpoint = admin.GetEndPoints().Single();
        var server = admin.GetServer(endpoint);
        await server.ExecuteAsync("CLIENT", "PAUSE", 500, "ALL");

        await using var adapter = new RedisWorkerCoordination(
            executor,
            keys,
            capacity: 512,
            maximumConcurrency: 16);
        var elapsed = Stopwatch.StartNew();
        var healthy = await adapter.CheckHealthAsync(
            CoordinationDeadline.FromNow(
                TimeSpan.FromMilliseconds(100)));
        elapsed.Stop();
        Check.True(
            !healthy,
            "slow established Redis fails the coordination health check");
        Check.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(1),
            "slow Redis obeys the caller's finite deadline");

        await Task.Delay(650);
        Check.True(
            await adapter.CheckHealthAsync(Deadline),
            "established executor recovers after Redis resumes");
    }

    private static WorldInstanceRuntimeOptions WorldOptions(
        ServerNodeId node,
        CoordinatedWorldRoute route) =>
        new()
        {
            ServerNodeId = node.ToString(),
            StaticOpenWorldInstances =
            [
                new StaticOpenWorldInstanceOptions
                {
                    RealmId = route.RealmId.Value,
                    MapId = route.MapId.Value,
                    WorldInstanceId =
                        route.WorldInstanceId.Value.ToString()
                }
            ]
        };

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout)
    {
        using var stop = new CancellationTokenSource(timeout);
        while (!stop.IsCancellationRequested)
        {
            if (await predicate())
            {
                return;
            }
            try
            {
                await Task.Delay(100, stop.Token);
            }
            catch (OperationCanceledException)
                when (stop.IsCancellationRequested)
            {
                break;
            }
        }
        throw new InvalidOperationException(
            "Timed out waiting for live Redis coordination recovery.");
    }
}
