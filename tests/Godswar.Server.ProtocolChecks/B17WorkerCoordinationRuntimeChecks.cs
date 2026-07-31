using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Coordination;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B17WorkerCoordinationRuntimeChecks
{
    public const string CheckName =
        "B17 fenced worker and player coordination runtime";

    public static async Task RunAsync()
    {
        await CheckWorkerAndPlayerLifecycleAsync();
        await CheckSkewedWallClockRuntimeAsync();
        await CheckDefinitivePlayerLeaseLossAsync(
            CoordinationOperationStatus.NotFound);
        await CheckDefinitivePlayerLeaseLossAsync(
            CoordinationOperationStatus.Unavailable);
        await CheckDuplicateWorkerIncarnationAsync();
        CheckMonotonicLeaseBudgetIgnoresWallClockOffset();
        CheckLocalProviderHasNoRedisRequirement();
    }

    private static async Task CheckSkewedWallClockRuntimeAsync()
    {
        var authorityClock = new ManualTimeProvider();
        var aheadClock = new WallClockOffsetTimeProvider(
            authorityClock,
            TimeSpan.FromDays(365));
        await using var adapter = new InMemoryWorkerCoordination(
            capacity: 16,
            maximumConcurrentOperations: 4,
            authorityClock);
        await using var runtime = new WorkerCoordinationRuntime(
            adapter,
            CreateOptions(),
            CreateWorldOptions("worker-clock-ahead"),
            contentRevision: "content-clock",
            buildRevision: "build-clock",
            aheadClock);
        using var stop = new CancellationTokenSource();
        var run = runtime.RunAsync(stop.Token);
        await runtime.WaitUntilRegisteredAsync().WaitAsync(
            TimeSpan.FromSeconds(1));
        await runtime.PublishAvailableAsync();
        Check.True(
            runtime.IsReady,
            "worker readiness uses monotonic lease time under +365d skew");
        Check.True(
            runtime.TryResolveRoute(1, out var route),
            "skewed runtime resolves its exact route");
        await using var player = await runtime.AcquireAsync(
            accountId: 71,
            characterId: 171,
            new PlayerOwnershipFence(Guid.NewGuid(), 1),
            route,
            static () => { });
        Check.True(
            player is not null && player.IsCurrent,
            "player lease uses monotonic time under +365d skew");

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static void CheckMonotonicLeaseBudgetIgnoresWallClockOffset()
    {
        var clock = new ManualTimeProvider();
        var ahead = new WallClockOffsetTimeProvider(
            clock,
            TimeSpan.FromDays(365));
        var behind = new WallClockOffsetTimeProvider(
            clock,
            TimeSpan.FromDays(-365));
        var aheadBudget = MonotonicLeaseBudget.Capture(
            ahead,
            TimeSpan.FromSeconds(5));
        var behindBudget = MonotonicLeaseBudget.Capture(
            behind,
            TimeSpan.FromSeconds(5));
        Check.True(
            aheadBudget.IsCurrent(ahead) &&
            behindBudget.IsCurrent(behind),
            "wall-clock offsets do not alter a monotonic lease budget");
        clock.Advance(TimeSpan.FromSeconds(5));
        Check.True(
            !aheadBudget.IsCurrent(ahead) &&
            !behindBudget.IsCurrent(behind),
            "monotonic lease budgets fail closed at their TTL boundary");
    }

    private static async Task CheckWorkerAndPlayerLifecycleAsync()
    {
        var clock = new ManualTimeProvider();
        var adapter = new InMemoryWorkerCoordination(
            capacity: 512,
            maximumConcurrentOperations: 8,
            clock);
        var options = CreateOptions();
        var world = CreateWorldOptions("worker-a");
        await using var runtime = new WorkerCoordinationRuntime(
            adapter,
            options,
            world,
            contentRevision: "content-test",
            buildRevision: "build-test",
            clock);
        using var stop = new CancellationTokenSource();
        var run = runtime.RunAsync(stop.Token);
        await runtime.WaitUntilRegisteredAsync().WaitAsync(
            TimeSpan.FromSeconds(1));

        Check.True(
            !runtime.IsReady,
            "registered worker remains not-ready before dependency release");
        Check.True(
            runtime.TryResolveRoute(1, out var route),
            "static legacy map resolves to one exact route");
        var initialRoute = await adapter.FindRouteAsync(
            route,
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(1),
                clock));
        Check.Equal(
            (int)CoordinatedWorkerState.Draining,
            (int)initialRoute.Route!.WorkerState,
            "initial worker registration fails closed as draining");
        var unavailable = await runtime.AcquireAsync(
            accountId: 6,
            characterId: 16,
            new PlayerOwnershipFence(Guid.NewGuid(), 6),
            route,
            static () => { });
        Check.True(
            unavailable is null,
            "initial draining registration rejects player admission");

        await runtime.PublishAvailableAsync();
        await runtime.WaitUntilReadyAsync().WaitAsync(
            TimeSpan.FromSeconds(1));

        Check.True(
            runtime.IsReady,
            "worker becomes ready only after explicit dependency release");
        var availableRoute = await adapter.FindRouteAsync(
            route,
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(1),
                clock));
        Check.Equal(
            (int)CoordinatedWorkerState.Available,
            (int)availableRoute.Route!.WorkerState,
            "explicit release publishes the worker as available");

        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 7);
        var lost = 0;
        await using var player = await runtime.AcquireAsync(
            accountId: 7,
            characterId: 17,
            ownership,
            route,
            () => Interlocked.Increment(ref lost));
        Check.True(player is not null, "PG-fenced player lease installs");
        Check.True(player!.IsCurrent, "installed player lease is current");
        Check.True(
            await player.PublishOnlineAsync(route),
            "world-ready player presence publishes");

        var current = await adapter.FindPlayerLeaseAsync(
            17,
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(1),
                clock));
        Check.Equal(
            (int)CoordinatedPresenceState.Online,
            (int)current.Lease!.Presence,
            "published presence is online");

        runtime.BeginDrain();
        await WaitUntilAsync(
            () => adapter.FindRouteAsync(
                        route,
                        CoordinationDeadline.FromNow(
                            TimeSpan.FromSeconds(1),
                            clock))
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                    .Route
                    ?.WorkerState ==
                CoordinatedWorkerState.Draining);
        var drainingRoute = await adapter.FindRouteAsync(
            route,
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(1),
                clock));
        Check.Equal(
            (int)CoordinatedWorkerState.Draining,
            (int)drainingRoute.Route!.WorkerState,
            "prompt drain publication stops new route admission");
        var republished = false;
        try
        {
            await runtime.PublishAvailableAsync();
            republished = true;
        }
        catch (InvalidOperationException)
        {
        }
        Check.True(
            !republished,
            "a draining worker cannot republish availability");
        Check.Equal(0, lost, "graceful drain does not steal current fence");

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        var released = await adapter.FindRouteAsync(
            route,
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(1),
                clock));
        Check.Equal(
            (int)CoordinationOperationStatus.NotFound,
            (int)released.Status,
            "shutdown removes the drained worker route");
    }

    private static async Task CheckDuplicateWorkerIncarnationAsync()
    {
        var clock = new ManualTimeProvider();
        await using var adapter = new InMemoryWorkerCoordination(
            512,
            8,
            clock);
        var route = Route();
        var first = await adapter.RegisterWorkerAsync(
            Registration("worker-a", Guid.NewGuid(), route),
            TimeSpan.FromSeconds(20),
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(1),
                clock));
        var second = await adapter.RegisterWorkerAsync(
            Registration("worker-a", Guid.NewGuid(), route),
            TimeSpan.FromSeconds(20),
            CoordinationDeadline.FromNow(
                TimeSpan.FromSeconds(1),
                clock));

        Check.True(first.Succeeded, "first worker incarnation registers");
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)second.Status,
            "duplicate live node incarnation is rejected");
    }

    private static void CheckLocalProviderHasNoRedisRequirement()
    {
        var options = new CoordinationRuntimeOptions
        {
            Provider = "Local",
            ConnectionStringEnvironmentVariable =
                "GODSWAR_B17_TEST_REDIS_MISSING"
        };
        Environment.SetEnvironmentVariable(
            options.ConnectionStringEnvironmentVariable,
            null);
        options.NormalizeAndValidate();
        Check.Equal(
            string.Empty,
            options.ConnectionString,
            "local fallback never resolves a Redis connection");
    }

    private static WorkerRegistrationRequest Registration(
        string node,
        Guid boot,
        CoordinatedWorldRoute route) =>
        new()
        {
            NodeId = new ServerNodeId(node),
            BootId = boot,
            BuildRevision = "build-test",
            ContentRevision = "content-test",
            State = CoordinatedWorkerState.Available,
            Capabilities = ["open-world-v1"],
            Routes = [route]
        };

    private static CoordinationRuntimeOptions CreateOptions() =>
        new()
        {
            Provider = "Local",
            Capacity = 512,
            MaximumConcurrentOperations = 8,
            ServerHeartbeatSeconds = 1,
            ServerTtlSeconds = 5,
            PlayerLeaseRenewalSeconds = 1,
            PlayerLeaseTtlSeconds = 5
        };

    private static WorldInstanceRuntimeOptions CreateWorldOptions(
        string node) =>
        new()
        {
            ServerNodeId = node,
            StaticOpenWorldInstances =
            [
                new StaticOpenWorldInstanceOptions
                {
                    RealmId = RealmId.Tempest.Value,
                    MapId = 1,
                    WorldInstanceId =
                        Route().WorldInstanceId.Value.ToString()
                }
            ]
        };

    private static CoordinatedWorldRoute Route() =>
        new(
            RealmId.Tempest,
            MapId.FromLegacy(1),
            new WorldInstanceId(
                Guid.Parse(
                    "11111111-1111-1111-1111-111111111111")));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (predicate())
            {
                return;
            }
            await Task.Yield();
        }

        throw new InvalidOperationException(
            "Timed out waiting for coordination state.");
    }

    private sealed class WallClockOffsetTimeProvider(
        ManualTimeProvider inner,
        TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            inner.GetUtcNow() + offset;

        public override long GetTimestamp() =>
            inner.GetTimestamp();

        public override long TimestampFrequency =>
            inner.TimestampFrequency;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }
}
