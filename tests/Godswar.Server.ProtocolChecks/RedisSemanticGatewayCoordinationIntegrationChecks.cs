using System.Net;
using Godswar.Server;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Redis;
using Godswar.Server.Networking.SemanticGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisSemanticGatewayCoordinationIntegrationChecks
{
    public const string CheckName =
        "B17 Redis semantic gateway cross-process authority";
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_REDIS_CONNECTION_STRING";
    private static readonly RealmId Realm = RealmId.Tempest;
    private static readonly MapId Sparta = new(0);
    private static readonly ServerNodeId Node = new("redis-worker-a");
    private static readonly WorldInstanceId World =
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP Redis semantic-gateway integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        var environment =
            "sg-" + Guid.NewGuid().ToString("N")[..12];
        var options = CreateOptions(
            environment,
            connectionString);
        await using var executorA =
            await RedisCoordinationExecutor.ConnectAsync(
                options,
                "semantic-test-a");
        await using var executorB =
            await RedisCoordinationExecutor.ConnectAsync(
                options,
                "semantic-test-b");
        var keys = new RedisCoordinationKeyBuilder(environment);
        await using var worker = new RedisWorkerCoordination(
            executorA,
            keys,
            capacity: 64,
            maximumConcurrency:
                options.MaximumConcurrentOperations);
        var registration = Registration(Guid.NewGuid());
        var workerLease = await worker.RegisterWorkerAsync(
            registration,
            TimeSpan.FromSeconds(5),
            Deadline(),
            CancellationToken.None);
        Check.True(
            workerLease.Succeeded,
            "real Redis publishes the exact worker route heartbeat " +
            $"({workerLease.Status})");

        var limits = new SemanticGatewayAuthorityLimits(
            maximumLoginGenerations: 32,
            maximumAdmissions: 32,
            maximumAdmissionsPerGeneration: 1,
            loginGenerationTtl: TimeSpan.FromSeconds(8),
            reservationTtl: TimeSpan.FromSeconds(3),
            committedAdmissionTtl: TimeSpan.FromSeconds(5));
        await using var gatewayA =
            new RedisSemanticGatewayCoordination(
                executorA,
                keys,
                Directory(),
                limits);
        await using var gatewayB =
            new RedisSemanticGatewayCoordination(
                executorB,
                keys,
                Directory(),
                limits);

        var principal =
            new SemanticGatewayPrincipal(701, "REDIS_GATEWAY");
        var login = await gatewayA.StartLoginAsync(
            principal,
            Source("192.0.2.41"),
            SemanticGatewayTestRealm.TempestGrant,
            Deadline(),
            CancellationToken.None);
        Check.True(
            login.IsStarted &&
            (await gatewayB.FindActivatedLoginAsync(
                "redis_gateway",
                login.Generation!.LoginSource.Address!,
                Deadline(),
                CancellationToken.None)).Status ==
                SemanticGatewayLoginLookupStatus.NotActivated,
            "another gateway sees pending login by hashed canonical name");
        Check.True(
            !await gatewayB.ActivateLoginAsync(
                login.Generation! with
                {
                    RealmGrant =
                        SemanticGatewayTestRealm.DwargonGrant
                },
                Deadline(),
                CancellationToken.None),
            "Redis rejects a tampered selected realm grant");
        Check.True(
            await gatewayB.ActivateLoginAsync(
                login.Generation!,
                Deadline(),
                CancellationToken.None),
            "another gateway atomically activates the exact generation");
        var activatedLookup =
            await gatewayA.FindActivatedLoginAsync(
                "REDIS_GATEWAY",
                login.Generation.LoginSource.Address!,
                Deadline(),
                CancellationToken.None);
        Check.True(
            activatedLookup.IsFound &&
            activatedLookup.Generation!.RealmGrant ==
                SemanticGatewayTestRealm.TempestGrant,
            "Redis round-trips the exact selected realm grant");
        Check.True(
            (await gatewayA.FindActivatedLoginAsync(
                "REDIS_GATEWAY",
                IPAddress.Parse("192.0.2.99"),
                Deadline(),
                CancellationToken.None)).Status ==
                SemanticGatewayLoginLookupStatus.SourceAddressMismatch,
            "cross-gateway lookup preserves exact observed source context");

        var reserveA = gatewayA.ReserveAdmissionAsync(
            login.Generation!.GenerationId,
            principal,
            Source("192.0.2.51"),
            Target(),
            Deadline(),
            CancellationToken.None).AsTask();
        var reserveB = gatewayB.ReserveAdmissionAsync(
            login.Generation.GenerationId,
            principal,
            Source("192.0.2.52"),
            Target(),
            Deadline(),
            CancellationToken.None).AsTask();
        var raced = await Task.WhenAll(reserveA, reserveB);
        Check.Equal(
            1,
            raced.Count(static value =>
                value.Status ==
                SemanticGatewayAdmissionStatus.Reserved),
            "two gateway adapters produce one reservation winner");
        Check.Equal(
            1,
            raced.Count(static value =>
                value.Status ==
                SemanticGatewayAdmissionStatus
                    .GenerationCapacityExceeded),
            "single-use generation rejects the losing reservation");

        var reserved = raced.Single(static value =>
            value.Status ==
            SemanticGatewayAdmissionStatus.Reserved);
        var claim = Claim(reserved.Admission!);
        var committed = await gatewayB.CommitAdmissionAsync(
            claim,
            Deadline(),
            CancellationToken.None);
        Check.True(
            committed.Status ==
                SemanticGatewayAdmissionStatus.Committed,
            "reservation commits across gateway adapters");
        _ = await executorA.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.KeyExpireAsync(
                keys.GatewayCounters(),
                TimeSpan.FromMilliseconds(250)));
        _ = await executorA.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.KeyExpireAsync(
                keys.GatewayExpiry(),
                TimeSpan.FromMilliseconds(250)));
        var refreshed = await gatewayA.RefreshAdmissionAsync(
            claim,
            Deadline(),
            CancellationToken.None);
        var counterTtl = await executorA.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.KeyTimeToLiveAsync(
                keys.GatewayCounters()));
        var expiryTtl = await executorA.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.KeyTimeToLiveAsync(
                keys.GatewayExpiry()));
        Check.True(
            refreshed.Status ==
                SemanticGatewayAdmissionStatus.Refreshed &&
            counterTtl > TimeSpan.FromHours(24) &&
            expiryTtl > TimeSpan.FromHours(24) &&
            (await gatewayA.ResolveAdmissionAsync(
                claim,
                Deadline(),
                CancellationToken.None)).Status ==
                SemanticGatewayAdmissionStatus.Committed,
            "refresh preserves capacity counters and expiry state for the " +
            "full admission lifetime");

        var replacement = await gatewayB.StartLoginAsync(
            principal,
            Source("192.0.2.61"),
            SemanticGatewayTestRealm.TempestGrant,
            Deadline(),
            CancellationToken.None);
        Check.True(
            replacement.IsStarted &&
            replacement.InvalidatedAdmissions == 1 &&
            (await gatewayA.ResolveAdmissionAsync(
                claim,
                Deadline(),
                CancellationToken.None)).Status ==
                SemanticGatewayAdmissionStatus.AdmissionNotFound,
            "duplicate login atomically supersedes the old admission");
        var activationRace = await Task.WhenAll(
            gatewayA.ActivateLoginAsync(
                replacement.Generation!,
                Deadline(),
                CancellationToken.None).AsTask(),
            gatewayB.ActivateLoginAsync(
                replacement.Generation!,
                Deadline(),
                CancellationToken.None).AsTask());
        Check.Equal(
            1,
            activationRace.Count(static value => value),
            "login activation is consume-once across gateway adapters");
        Check.True(
            await gatewayA.CancelLoginAsync(
                replacement.Generation!,
                Deadline(),
                CancellationToken.None),
            "cross-gateway cancellation removes the exact generation");

        await CheckRollbackCleanupAsync(
            gatewayA,
            gatewayB,
            executorA,
            keys);
        await CheckDrainingAndBootFenceAsync(
            gatewayA,
            gatewayB,
            worker,
            workerLease.Lease ??
            throw new InvalidOperationException(
                "Worker registration returned no lease."),
            principal);
        await CheckAtomicRouteProofTransitionsAsync(
            gatewayA,
            worker,
            executorA,
            keys,
            limits);
        await CheckRedisClockAuthorityAsync(connectionString);
        await executorA.ExecuteAsync(
            RedisCoordinationOperationFamily.Admission,
            Deadline(),
            database => database.KeyDeleteAsync(
                [
                    keys.GatewayCounters(),
                    keys.GatewayExpiry()
                ]));
    }

    private static async Task<SemanticGatewayLoginGenerationLease>
        StartActivatedAsync(
            RedisSemanticGatewayCoordination gateway,
            SemanticGatewayPrincipal principal,
            string address)
    {
        var login = await gateway.StartLoginAsync(
            principal,
            Source(address),
            SemanticGatewayTestRealm.TempestGrant,
            Deadline(),
            CancellationToken.None);
        Check.True(
            login.IsStarted &&
            await gateway.ActivateLoginAsync(
                login.Generation!,
                Deadline(),
                CancellationToken.None),
            "fixture starts and activates one login generation");
        return login.Generation!;
    }

    private static CoordinationRuntimeOptions CreateOptions(
        string environment,
        string connectionString)
    {
        var options = new CoordinationRuntimeOptions
        {
            Provider = "Redis",
            Environment = environment,
            ConnectionStringEnvironmentVariable =
                ConnectionStringVariable,
            Capacity = 4_096,
            MaximumConcurrentOperations = 64,
            QueueAdmissionTimeoutMilliseconds = 250,
            OperationTimeoutMilliseconds = 2_000,
            ConnectTimeoutMilliseconds = 3_000,
            RequireTls = connectionString.Contains(
                "ssl=true",
                StringComparison.OrdinalIgnoreCase)
        };
        options.NormalizeAndValidate();
        return options;
    }

    private static WorkerRegistrationRequest Registration(Guid bootId) =>
        new()
        {
            NodeId = Node,
            BootId = bootId,
            BuildRevision = "semantic-test",
            ContentRevision = "semantic-test",
            State = CoordinatedWorkerState.Available,
            Capabilities = ["semantic-gateway-test"],
            Routes =
            [
                new CoordinatedWorldRoute(Realm, Sparta, World)
            ]
        };

    private static StaticSemanticGatewayRouteDirectory Directory() =>
        new(
            [new SemanticGatewayWorkerDefinition(Node, 1)],
            [
                new SemanticGatewayStaticRoute(
                    Realm,
                    Sparta,
                    World,
                    Node,
                    1)
            ]);

    private static SemanticGatewayRouteTarget Target() =>
        new(Realm, Sparta, World);

    private static SemanticGatewayConnectionSource Source(string address) =>
        new(
            GatewayConnectionId.New(),
            IPAddress.Parse(address));

    private static SemanticGatewayAdmissionClaim Claim(
        SemanticGatewayAdmissionLease lease) =>
        new(
            lease.AdmissionId,
            lease.GenerationId,
            lease.Principal,
            lease.Source,
            lease.Route.Target,
            lease.Route.NodeId,
            lease.Route.WorkerRevision);

    private static CoordinationDeadline Deadline() =>
        CoordinationDeadline.FromNow(TimeSpan.FromSeconds(4));
}
