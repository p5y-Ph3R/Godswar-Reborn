using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisWorkerCoordinationIntegrationChecks
{
    public const string CheckName =
        "Redis fenced worker route and player lease authority";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_REDIS_CONNECTION_STRING";

    private static CoordinationDeadline Deadline =>
        CoordinationDeadline.FromNow(TimeSpan.FromSeconds(2));

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

        var environment = $"worker_test_{Guid.NewGuid():N}"[..29];
        var options = CreateOptions(connectionString, environment);
        await using var executor =
            await RedisCoordinationExecutor.ConnectAsync(
                options,
                "b17-worker-test");
        var keys = new RedisCoordinationKeyBuilder(environment);
        var route = new CoordinatedWorldRoute(
            RealmId.Tempest,
            MapId.FromLegacy(1),
            new WorldInstanceId(Guid.NewGuid()));
        var nodeId = new ServerNodeId("b17-worker-a");
        var bootId = Guid.NewGuid();
        var cleanup = new HashSet<string>(StringComparer.Ordinal)
        {
            keys.Worker(nodeId),
            keys.RealmContent(route.RealmId),
            keys.Route(route.WorldInstanceId),
            keys.Player(700_017),
            keys.Player(700_018)
        };

        await using var first = new RedisWorkerCoordination(
            executor,
            keys,
            capacity: 512,
            maximumConcurrency: 16);
        await using var second = new RedisWorkerCoordination(
            executor,
            keys,
            capacity: 512,
            maximumConcurrency: 16);
        try
        {
            var registration = await first.RegisterWorkerAsync(
                Registration(nodeId, bootId, route),
                TimeSpan.FromSeconds(20),
                Deadline);
            Check.True(
                registration.Succeeded,
                "first worker incarnation atomically registers");
            var workerLease = registration.Lease!.Value;

            await CheckExactRouteAsync(first, route, nodeId, bootId);
            await CheckRealmContentAdmissionAsync(
                second,
                keys,
                route,
                cleanup);
            await CheckDuplicateIncarnationAsync(
                second,
                nodeId,
                route);
            var currentPlayer = await CheckFencedPlayerLeaseAsync(
                first,
                route,
                nodeId,
                bootId);
            await CheckDrainRejectsNewLeaseAsync(
                first,
                workerLease,
                route,
                nodeId,
                bootId);
            await CheckLiveRuntimeRecoveryAsync(
                executor,
                keys,
                options,
                cleanup);
            await CheckSlowRedisDeadlineAndRecoveryAsync(
                executor,
                keys);

            Check.Equal(
                (int)CoordinationOperationStatus.Applied,
                (int)await first.ReleasePlayerLeaseAsync(
                    currentPlayer,
                    Deadline),
                "current fenced player lease releases exactly");
            Check.Equal(
                (int)CoordinationOperationStatus.Applied,
                (int)await first.ReleaseWorkerAsync(
                    workerLease with
                    {
                        State = CoordinatedWorkerState.Draining
                    },
                    Deadline),
                "current worker incarnation releases exactly");
            var removed = await first.FindRouteAsync(route, Deadline);
            Check.Equal(
                (int)CoordinationOperationStatus.NotFound,
                (int)removed.Status,
                "worker release removes its exact route projection");
        }
        finally
        {
            await CleanupAsync(executor, cleanup);
        }
    }

    private static async Task CheckExactRouteAsync(
        IWorkerCoordination coordination,
        CoordinatedWorldRoute route,
        ServerNodeId nodeId,
        Guid bootId)
    {
        var found = await coordination.FindRouteAsync(route, Deadline);
        Check.True(found.IsFound, "registered route is discoverable");
        Check.True(
            found.Route!.NodeId == nodeId &&
            found.Route.BootId == bootId &&
            found.Route.Route == route &&
            found.Route.WorkerState ==
                CoordinatedWorkerState.Available,
            "route resolves to the exact live worker incarnation");
    }

    private static async Task CheckDuplicateIncarnationAsync(
        IWorkerCoordination coordination,
        ServerNodeId nodeId,
        CoordinatedWorldRoute route)
    {
        var duplicate = await coordination.RegisterWorkerAsync(
            Registration(nodeId, Guid.NewGuid(), route),
            TimeSpan.FromSeconds(20),
            Deadline);
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)duplicate.Status,
            "second live boot for one node is rejected");
    }

    private static async Task CheckRealmContentAdmissionAsync(
        IWorkerCoordination coordination,
        RedisCoordinationKeyBuilder keys,
        CoordinatedWorldRoute existingRoute,
        ISet<string> cleanup)
    {
        var shortRoute = new CoordinatedWorldRoute(
            existingRoute.RealmId,
            MapId.FromLegacy(2),
            new WorldInstanceId(Guid.NewGuid()));
        var shortNode = new ServerNodeId("b17-worker-short-ttl");
        cleanup.Add(keys.Worker(shortNode));
        cleanup.Add(keys.Route(shortRoute.WorldInstanceId));
        var same = await coordination.RegisterWorkerAsync(
            Registration(
                shortNode,
                Guid.NewGuid(),
                shortRoute,
                "content-test"),
            TimeSpan.FromSeconds(1),
            Deadline);
        Check.True(
            same.Succeeded,
            "same Redis realm content admits a disjoint-map worker");

        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        var mismatchRoute = new CoordinatedWorldRoute(
            existingRoute.RealmId,
            MapId.FromLegacy(3),
            new WorldInstanceId(Guid.NewGuid()));
        var mismatchNode = new ServerNodeId("b17-worker-mismatch");
        cleanup.Add(keys.Worker(mismatchNode));
        cleanup.Add(keys.Route(mismatchRoute.WorldInstanceId));
        var mismatch = await coordination.RegisterWorkerAsync(
            Registration(
                mismatchNode,
                Guid.NewGuid(),
                mismatchRoute,
                "content-new"),
            TimeSpan.FromSeconds(5),
            Deadline);
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)mismatch.Status,
            "short Redis worker TTL cannot shorten live realm admission");

        var otherRealmRoute = new CoordinatedWorldRoute(
            new RealmId(existingRoute.RealmId.Value + 1),
            MapId.FromLegacy(3),
            new WorldInstanceId(Guid.NewGuid()));
        var otherNode = new ServerNodeId("b17-worker-other-realm");
        cleanup.Add(keys.Worker(otherNode));
        cleanup.Add(keys.Route(otherRealmRoute.WorldInstanceId));
        cleanup.Add(keys.RealmContent(otherRealmRoute.RealmId));
        var other = await coordination.RegisterWorkerAsync(
            Registration(
                otherNode,
                Guid.NewGuid(),
                otherRealmRoute,
                "content-new"),
            TimeSpan.FromSeconds(5),
            Deadline);
        Check.True(
            other.Succeeded,
            "different Redis realms may pin different content");
    }

    private static async Task<CoordinatedPlayerLease>
        CheckFencedPlayerLeaseAsync(
            IWorkerCoordination coordination,
            CoordinatedWorldRoute route,
            ServerNodeId nodeId,
        Guid bootId)
    {
        var firstFence =
            new PlayerOwnershipFence(Guid.NewGuid(), 7);
        var firstToken = Guid.NewGuid();
        var first = await coordination.InstallPlayerLeaseAsync(
            PlayerRequest(
                700_017,
                firstFence,
                firstToken,
                nodeId,
                bootId,
                route),
            TimeSpan.FromSeconds(30),
            Deadline);
        Check.True(first.Succeeded, "PostgreSQL-issued fence installs");
        var sameIdentityDowngrade =
            await coordination.InstallPlayerLeaseAsync(
                PlayerRequest(
                    700_017,
                    firstFence with { Generation = 6 },
                    firstToken,
                    nodeId,
                    bootId,
                    route),
                TimeSpan.FromSeconds(30),
                Deadline);
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)sameIdentityDowngrade.Status,
            "same lease identity cannot downgrade its PostgreSQL generation");
        var afterDowngrade =
            await coordination.FindPlayerLeaseAsync(700_017, Deadline);
        Check.True(
            afterDowngrade.IsFound &&
            afterDowngrade.Lease!.Ownership.Generation == 7,
            "rejected generation downgrade preserves the current fence");
        var stale = await coordination.InstallPlayerLeaseAsync(
            PlayerRequest(
                700_017,
                new PlayerOwnershipFence(Guid.NewGuid(), 6),
                Guid.NewGuid(),
                nodeId,
                bootId,
                route),
            TimeSpan.FromSeconds(30),
            Deadline);
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)stale.Status,
            "lower PostgreSQL generation cannot steal a player");

        var successor = await coordination.InstallPlayerLeaseAsync(
            PlayerRequest(
                700_017,
                new PlayerOwnershipFence(Guid.NewGuid(), 8),
                Guid.NewGuid(),
                nodeId,
                bootId,
                route),
            TimeSpan.FromSeconds(30),
            Deadline);
        Check.True(
            successor.Succeeded,
            "higher PostgreSQL generation replaces stale coordination");
        var successorLease = successor.Lease ??
            throw new InvalidOperationException(
                "Successful successor lease had no value.");
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)await coordination.ReleasePlayerLeaseAsync(
                first.Lease!,
                Deadline),
            "stale release cannot delete the successor");

        var online = await coordination.RenewPlayerLeaseAsync(
            successorLease,
            route,
            CoordinatedPresenceState.Online,
            TimeSpan.FromSeconds(30),
            Deadline);
        var onlineLease = online.Lease ??
            throw new InvalidOperationException(
                "Successful online renewal had no value.");
        Check.True(
            online.Succeeded &&
            onlineLease.Presence ==
                CoordinatedPresenceState.Online &&
            onlineLease.Version > successorLease.Version,
            "current lease publishes ordered online presence");
        var lookup = await coordination.FindPlayerLeaseAsync(
            700_017,
            Deadline);
        Check.True(
            lookup.IsFound &&
            lookup.Lease!.Ownership.Generation == 8 &&
            lookup.Lease.Presence == CoordinatedPresenceState.Online,
            "lookup returns only the current fenced presence");
        return onlineLease;
    }

    private static async Task CheckDrainRejectsNewLeaseAsync(
        IWorkerCoordination coordination,
        WorkerRegistrationLease workerLease,
        CoordinatedWorldRoute route,
        ServerNodeId nodeId,
        Guid bootId)
    {
        var draining = await coordination.RenewWorkerAsync(
            workerLease,
            CoordinatedWorkerState.Draining,
            TimeSpan.FromSeconds(20),
            Deadline);
        Check.True(
            draining.Succeeded &&
            draining.Lease!.Value.State ==
                CoordinatedWorkerState.Draining,
            "worker heartbeat publishes draining state");
        var routeLookup =
            await coordination.FindRouteAsync(route, Deadline);
        Check.True(
            routeLookup.IsFound &&
            routeLookup.Route!.WorkerState ==
                CoordinatedWorkerState.Draining,
            "route lookup observes the worker drain");
        var rejected = await coordination.InstallPlayerLeaseAsync(
            PlayerRequest(
                700_018,
                new PlayerOwnershipFence(Guid.NewGuid(), 1),
                Guid.NewGuid(),
                nodeId,
                bootId,
                route),
            TimeSpan.FromSeconds(30),
            Deadline);
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)rejected.Status,
            "draining worker rejects new player ownership");
    }

    private static WorkerRegistrationRequest Registration(
        ServerNodeId nodeId,
        Guid bootId,
        CoordinatedWorldRoute route,
        string contentRevision = "content-test") =>
        new()
        {
            NodeId = nodeId,
            BootId = bootId,
            BuildRevision = "b17-test",
            ContentRevision = contentRevision,
            State = CoordinatedWorkerState.Available,
            Capabilities = ["open-world-v1"],
            Routes = [route]
        };

    private static PlayerLeaseInstallRequest PlayerRequest(
        int characterId,
        PlayerOwnershipFence ownership,
        Guid token,
        ServerNodeId nodeId,
        Guid bootId,
        CoordinatedWorldRoute route) =>
        new()
        {
            AccountId = 700_001,
            CharacterId = characterId,
            Ownership = ownership,
            LeaseToken = token,
            NodeId = nodeId,
            WorkerBootId = bootId,
            Route = route,
            Presence = CoordinatedPresenceState.EnteringWorld
        };

    private static CoordinationRuntimeOptions CreateOptions(
        string connectionString,
        string environment)
    {
        const string variable = "GODSWAR_B17_WORKER_REDIS";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(
                variable,
                connectionString);
            var parsed = ConfigurationOptions.Parse(connectionString);
            var options = new CoordinationRuntimeOptions
            {
                Provider = "Redis",
                Environment = environment,
                ConnectionStringEnvironmentVariable = variable,
                Capacity = 512,
                MaximumConcurrentOperations = 16,
                QueueAdmissionTimeoutMilliseconds = 100,
                OperationTimeoutMilliseconds = 2_000,
                ConnectTimeoutMilliseconds = 3_000,
                CircuitFailureThreshold = 5,
                CircuitOpenMilliseconds = 1_000,
                RequireTls = parsed.Ssl
            };
            options.NormalizeAndValidate();
            return options;
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static async Task CleanupAsync(
        RedisCoordinationExecutor executor,
        IEnumerable<string> cleanup)
    {
        try
        {
            await executor.ExecuteAsync(
                RedisCoordinationOperationFamily.Health,
                Deadline,
                database => database.KeyDeleteAsync(
                    cleanup.Select(
                            static value => (RedisKey)value)
                        .ToArray()));
        }
        catch
        {
        }
    }
}
