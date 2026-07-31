using System.Xml.Linq;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// ADR 0005 supersedes the historical B16 defer ratchet. This check keeps
/// Redis disposable, opt-in, bounded, and outside gameplay/ECS code.
/// </summary>
internal static class DeferredRedisArchitectureChecks
{
    public const string CheckName =
        "B17 Redis coordination architecture ratchet";

    public static Task RunAsync()
    {
        var root = FindRepositoryRoot();
        CheckReviewedClient(root);
        CheckOptInComposition(root);
        CheckAsyncContracts(root);
        CheckGameplayHasNoRedisDriver(root);
        CheckDurableFenceOrdering(root);
        CheckAtomicCoordinationAndSupervision(root);
        CheckDisposableRuntime(root);
        return Task.CompletedTask;
    }

    private static void CheckReviewedClient(string root)
    {
        var project = XDocument.Load(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Godswar.Server.csproj"));
        var packages = project.Descendants()
            .Where(element =>
                element.Name.LocalName == "PackageReference")
            .Select(element => new
            {
                Name =
                    element.Attribute("Include")?.Value ??
                    element.Attribute("Update")?.Value,
                Version = element.Attribute("Version")?.Value
            })
            .Where(package => string.Equals(
                package.Name,
                "StackExchange.Redis",
                StringComparison.Ordinal))
            .ToArray();
        Check.Equal(1, packages.Length, "one reviewed Redis client");
        Check.True(
            !string.IsNullOrWhiteSpace(packages[0].Version),
            "Redis client version is pinned");
    }

    private static void CheckOptInComposition(string root)
    {
        var options = Read(root, "src/Godswar.Server/" +
            "CoordinationRuntimeOptions.cs");
        var composition = Read(root, "src/Godswar.Server/" +
            "ServerCoordinationComposition.cs");
        Check.True(
            options.Contains(
                "public string Provider { get; set; } = \"Local\";",
                StringComparison.Ordinal),
            "coordination defaults to local");
        Check.True(
            composition.Contains(
                "CoordinationProviderKind.Local",
                StringComparison.Ordinal) &&
            composition.Contains(
                "executor: null",
                StringComparison.Ordinal),
            "local fallback constructs no Redis executor");
        Check.True(
            composition.Contains(
                "RedisGameTicketStore",
                StringComparison.Ordinal) &&
            composition.Contains(
                "RedisWorkerCoordination",
                StringComparison.Ordinal),
            "Redis provider composes ticket and worker adapters");
    }

    private static void CheckAsyncContracts(string root)
    {
        var tickets = Read(root, "src/Godswar.Server/Application/" +
            "Sessions/IGameTicketStore.cs");
        var coordination = Read(root, "src/Godswar.Server/Application/" +
            "Coordination/WorkerCoordinationContracts.cs");
        foreach (var source in new[] { tickets, coordination })
        {
            Check.True(
                source.Contains("ValueTask", StringComparison.Ordinal) &&
                source.Contains("CancellationToken", StringComparison.Ordinal),
                "coordination contracts are async and cancellable");
        }
        Check.True(
            tickets.Contains(
                "SecureTicketOperationDeadline",
                StringComparison.Ordinal) &&
            coordination.Contains(
                "CoordinationDeadline",
                StringComparison.Ordinal),
            "external coordination calls carry finite deadlines");
    }

    private static void CheckGameplayHasNoRedisDriver(string root)
    {
        var forbidden = new[]
        {
            Path.Combine(root, "src", "Godswar.Server", "Game"),
            Path.Combine(root, "src", "Godswar.Server", "World"),
            Path.Combine(root, "src", "Godswar.Server", "Application")
        };
        foreach (var directory in forbidden)
        {
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(path);
                Check.True(
                    !source.Contains(
                        "StackExchange.Redis",
                        StringComparison.Ordinal) &&
                    !source.Contains(
                        "Infrastructure.Redis",
                        StringComparison.Ordinal),
                    $"{Path.GetRelativePath(root, path)} has no Redis driver");
            }
        }
    }

    private static void CheckDurableFenceOrdering(string root)
    {
        var handler = Read(root, "src/Godswar.Server/Game/" +
            "GameClientHandler.CharacterCheckpoints.cs");
        AssertOrdered(
            handler,
            "_characterCheckpoints.AcquireAsync(",
            "_playerCoordination.AcquireAsync(",
            "_registry.TryBindAccountSessionOwnership(");
        AssertOrdered(
            handler,
            "ReleasePlayerCoordinationLeaseAsync();",
            "ReleaseCheckpointOwnershipAsync(");
    }

    private static void CheckDisposableRuntime(string root)
    {
        var compose = Read(
            root,
            "docker-compose.redis-coordination.yml");
        var redis = Read(
            root,
            "ops/redis/redis-coordination.local.conf");
        var generator = Read(
            root,
            "tools/NewB17RedisLocalConfiguration.ps1");
        var gate = Read(
            root,
            "tools/InvokeB17RedisCiGate.ps1");
        Check.True(
            compose.Contains("127.0.0.1", StringComparison.Ordinal) &&
            compose.Contains("no-new-privileges:true", StringComparison.Ordinal),
            "local Redis is loopback-only and drops privilege");
        Check.True(
            redis.Contains("save \"\"", StringComparison.Ordinal) &&
            redis.Contains("appendonly no", StringComparison.Ordinal) &&
            redis.Contains(
                "maxmemory-policy noeviction",
                StringComparison.Ordinal) &&
            redis.Contains("aclfile ", StringComparison.Ordinal),
            "coordination Redis is disposable, noeviction, and ACL protected");
        Check.True(
            generator.Contains(
                "user default off",
                StringComparison.Ordinal) &&
            generator.Contains(
                "~godswar:b17-local:v1:*",
                StringComparison.Ordinal) &&
            generator.Contains(
                "-@all",
                StringComparison.Ordinal) &&
            generator.Contains(
                "+eval",
                StringComparison.Ordinal) &&
            generator.Contains(
                "+time",
                StringComparison.Ordinal) &&
            !generator.Contains(
                "+@read",
                StringComparison.Ordinal) &&
            !generator.Contains(
                "+@write",
                StringComparison.Ordinal) &&
            !generator.Contains(
                "+@scripting",
                StringComparison.Ordinal),
            "generated application ACL is environment-scoped with an " +
            "explicit command allowlist");
        Check.True(
            gate.Contains(
                "Assert-ApplicationAclBoundary",
                StringComparison.Ordinal) &&
            gate.Contains(
                "Assert-RestartStateLoss",
                StringComparison.Ordinal) &&
            gate.Contains(
                "liveTicketContinuityClaimed = $false",
                StringComparison.Ordinal),
            "mandatory gate proves ACL denial and disposable restart loss");
    }

    private static void CheckAtomicCoordinationAndSupervision(string root)
    {
        var playerAdapter = Read(
            root,
            "src/Godswar.Server/Infrastructure/Redis/" +
            "RedisWorkerCoordination.PlayerLeases.cs");
        var executor = Read(
            root,
            "src/Godswar.Server/Infrastructure/Redis/" +
            "RedisCoordinationExecutor.cs");
        var playerScripts = Read(
            root,
            "src/Godswar.Server/Infrastructure/Redis/" +
            "RedisCoordinationScripts.cs");
        Check.True(
            !playerAdapter.Contains(
                "FindRouteAsync(",
                StringComparison.Ordinal) &&
            playerAdapter.Contains(
                "_keys.Route(",
                StringComparison.Ordinal) &&
            playerAdapter.Contains(
                "_keys.Worker(",
                StringComparison.Ordinal) &&
            playerScripts.Contains(
                "redis.call('HGET', KEYS[2], 'node')",
                StringComparison.Ordinal) &&
            playerScripts.Contains(
                "redis.call('HGET', KEYS[3], 'boot')",
                StringComparison.Ordinal),
            "player lease transitions validate route and worker proofs " +
            "inside one Redis script");
        Check.True(
            executor.Contains(
                "configuration.TieBreaker = string.Empty;",
                StringComparison.Ordinal),
            "single-primary coordination disables the client tie-breaker key");
        Check.True(
            executor.Contains(
                "configuration.CommandMap = SinglePrimaryCommandMap;",
                StringComparison.Ordinal) &&
            executor.Contains("\"CONFIG\"", StringComparison.Ordinal) &&
            executor.Contains("\"CLUSTER\"", StringComparison.Ordinal) &&
            executor.Contains("available: false", StringComparison.Ordinal),
            "single-primary coordination disables Redis topology/admin probes");

        var runtimeLease = Read(
            root,
            "src/Godswar.Server/Infrastructure/Coordination/" +
            "WorkerCoordinationRuntime.PlayerLease.cs");
        var renewStart = runtimeLease.IndexOf(
            "private async ValueTask<bool> RenewAsync(",
            StringComparison.Ordinal);
        Check.True(renewStart >= 0, "player renewal method exists");
        var renew = runtimeLease[renewStart..];
        AssertOrdered(
            renew,
            "_operationGate.WaitAsync(",
            "current = _lease;",
            "if (useLatestState)");

        var workerRuntime = Read(
            root,
            "src/Godswar.Server/Infrastructure/Coordination/" +
            "WorkerCoordinationRuntime.cs");
        Check.True(
            workerRuntime.Contains(
                "(int)CoordinatedWorkerState.Draining",
                StringComparison.Ordinal) &&
            workerRuntime.Contains(
                "public async Task PublishAvailableAsync(",
                StringComparison.Ordinal) &&
            workerRuntime.Contains(
                "QueueDrainPublication();",
                StringComparison.Ordinal),
            "worker registration is draining-first with explicit publish " +
            "and prompt shutdown drain");
        var program = Read(root, "src/Godswar.Server/Program.cs");
        AssertOrdered(
            program,
            "coordination.Worker.WaitUntilRegisteredAsync(",
            "workerBackhaulRuntime.Start(",
            "server.WaitUntilStartedAsync(",
            "coordination.Worker.PublishAvailableAsync(");

        var host = Read(
            root,
            "src/Godswar.Server/Networking/SemanticGateway/" +
            "SemanticGatewayHost.cs");
        Check.True(
            host.Contains(
                "Task.WhenAny(login, game, sweep)",
                StringComparison.Ordinal) &&
            host.Contains(
                "Task.WhenAll(login, game, sweep)",
                StringComparison.Ordinal),
            "gateway expiry cleanup is a supervised critical task");

        var admissionScript = Read(
            root,
            "src/Godswar.Server/Infrastructure/Redis/" +
            "RedisSemanticGatewayScripts.Admission.cs");
        Check.True(
            admissionScript.Contains(
                "redis.call('PEXPIRE', KEYS[5], ARGV[14])",
                StringComparison.Ordinal) &&
            admissionScript.Contains(
                "redis.call('PEXPIRE', KEYS[6], ARGV[14])",
                StringComparison.Ordinal),
            "active admission refresh preserves counter and expiry TTLs");

        var redisRoot =
            "src/Godswar.Server/Infrastructure/Redis/";
        var redisClockScripts = new[]
        {
            admissionScript,
            Read(
                root,
                redisRoot +
                "RedisSemanticGatewayScripts.Login.cs"),
            Read(
                root,
                redisRoot +
                "RedisSemanticGatewayScripts.Expiry.cs"),
            Read(root, redisRoot + "RedisCoordinationScripts.cs")
        };
        Check.True(
            redisClockScripts.All(static source =>
                source.Contains(
                    "redis.call('TIME')",
                    StringComparison.Ordinal)),
            "shared Redis lease scripts derive authoritative time from " +
            "Redis TIME");

        var workerAdapters = string.Concat(
            Read(
                root,
                redisRoot + "RedisWorkerCoordination.cs"),
            Read(
                root,
                redisRoot +
                "RedisWorkerCoordination.Routes.cs"),
            Read(
                root,
                redisRoot +
                "RedisWorkerCoordination.PlayerLeases.cs"));
        var semanticAdapters = string.Concat(
            Read(
                root,
                redisRoot +
                "RedisSemanticGatewayCoordination.Login.cs"),
            Read(
                root,
                redisRoot +
                "RedisSemanticGatewayCoordination.Admissions.cs"),
            Read(
                root,
                redisRoot +
                "RedisSemanticGatewayCoordination.Routes.cs"));
        Check.True(
            !workerAdapters.Contains(
                "DateTimeOffset.UtcNow",
                StringComparison.Ordinal) &&
            !workerAdapters.Contains(
                "UnixMilliseconds(",
                StringComparison.Ordinal) &&
            !semanticAdapters.Contains(
                "UnixMilliseconds(",
                StringComparison.Ordinal) &&
            !semanticAdapters.Contains(
                "_timeProvider.GetUtcNow()",
                StringComparison.Ordinal),
            "Redis callers cannot submit host wall-clock authority");

        Check.True(
            workerRuntime.Contains(
                "MonotonicLeaseBudget",
                StringComparison.Ordinal) &&
            runtimeLease.Contains(
                "MonotonicLeaseBudget",
                StringComparison.Ordinal) &&
            !workerRuntime.Contains(
                "ProvenUntilUtc > _timeProvider.GetUtcNow()",
                StringComparison.Ordinal) &&
            !runtimeLease.Contains(
                "_lease.ProvenUntilUtc >",
                StringComparison.Ordinal),
            "worker and player readiness use conservative monotonic budgets");

        var backhaulRegistry = Read(
            root,
            "src/Godswar.Server/Networking/Backhaul/" +
            "WorkerBackhaulAdmissionRegistry.cs");
        var backhaulRetry = Read(
            root,
            "src/Godswar.Server/Networking/SemanticGateway/" +
            "SemanticGatewayGameConnection.Backhaul.cs");
        Check.True(
            backhaulRegistry.Contains(
                "DueTimestamp",
                StringComparison.Ordinal) &&
            backhaulRegistry.Contains(
                "LocalReservationLifetime",
                StringComparison.Ordinal) &&
            !backhaulRegistry.Contains(
                "GetUtcNow()",
                StringComparison.Ordinal) &&
            backhaulRetry.Contains(
                "GetElapsedTime(startedAtTimestamp)",
                StringComparison.Ordinal) &&
            !backhaulRetry.Contains(
                "GetUtcNow()",
                StringComparison.Ordinal),
            "backhaul admission and retry lifetimes are receipt-relative " +
            "and monotonic");
    }

    private static void AssertOrdered(
        string source,
        params string[] tokens)
    {
        var prior = -1;
        foreach (var token in tokens)
        {
            var current = source.IndexOf(
                token,
                prior + 1,
                StringComparison.Ordinal);
            Check.True(
                current > prior,
                $"ordered coordination boundary contains '{token}'");
            prior = current;
        }
    }

    private static string Read(string root, string relative) =>
        File.ReadAllText(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "GodswarServer.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }
        throw new InvalidOperationException(
            "Could not locate the repository root.");
    }
}
