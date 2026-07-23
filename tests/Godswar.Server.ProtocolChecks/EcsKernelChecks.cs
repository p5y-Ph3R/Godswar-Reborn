using Godswar.Server.Ecs;

namespace Godswar.Server.ProtocolChecks;

internal static class EcsKernelChecks
{
    public static Task RunAsync()
    {
        CheckEntityGenerationLifecycle();
        CheckSparseComponentsAndQueries();
        CheckDeferredCommands();
        CheckTypedEvents();
        CheckOrderedScheduler();
        return Task.CompletedTask;
    }

    private static void CheckEntityGenerationLifecycle()
    {
        var registry = new EntityRegistry();
        var first = registry.Create();
        var second = registry.Create();

        Check.True(first.IsValid, "created entity handle is valid");
        Check.True(registry.IsAlive(first), "created entity is alive");
        Check.Equal(2, registry.Count, "entity registry live count");

        registry.Destroy(first);
        Check.True(!registry.IsAlive(first), "destroyed entity handle becomes stale");

        var replacement = registry.Create();
        Check.Equal(first.Index, replacement.Index, "free-list reuses released index");
        Check.True(
            first.Generation != replacement.Generation,
            "reused index receives a new generation");
        Check.True(
            !registry.TryDestroy(first),
            "stale handle cannot destroy its replacement");

        var ordered = registry.EnumerateAlive().ToArray();
        Check.Equal(replacement, ordered[0], "live enumeration starts at lowest index");
        Check.Equal(second, ordered[1], "live enumeration retains ascending index order");
    }

    private static void CheckSparseComponentsAndQueries()
    {
        var world = new EcsWorld();
        var positions = world.RegisterComponent<Position>();
        world.RegisterComponent<Health>();
        Check.True(
            ReferenceEquals(positions, world.RegisterComponent<Position>()),
            "component registration is idempotent");

        var first = world.CreateEntity();
        var second = world.CreateEntity();
        var third = world.CreateEntity();

        world.Add(third, new Position(30));
        world.Add(first, new Position(10));
        world.Add(first, new Health(100));
        world.Add(second, new Health(80));

        Check.Equal(
            2,
            positions.Count,
            "sparse component pool tracks its dense count");
        Check.True(world.Has<Position>(first), "world resolves a registered component");
        Check.Equal(10, world.Get<Position>(first).X, "component lookup returns its value");

        world.Get<Position>(first).X = 11;
        Check.Equal(11, world.Get<Position>(first).X, "component lookup supports ref mutation");

        var positionEntities = world.Query<Position>().ToArray();
        Check.Equal(first, positionEntities[0], "query ordering ignores component insertion order");
        Check.Equal(third, positionEntities[1], "query ordering is ascending by entity index");
        Check.Equal(
            first,
            world.Query<Position, Health>().Single(),
            "multi-component query returns the intersection");

        using var query = world.Query<Position>().GetEnumerator();
        Check.True(query.MoveNext(), "query fixture has a first result");
        world.Add(second, new Position(20));
        Check.Throws<InvalidOperationException>(
            () => query.MoveNext(),
            "structural component change invalidates an active query");

        world.DestroyEntity(first);
        Check.True(!world.IsAlive(first), "world destroys entity generation");
        Check.True(!world.Has<Position>(first), "entity destruction removes all components");
        Check.True(!world.Has<Health>(first), "entity destruction clears every pool");
        Check.Throws<InvalidOperationException>(
            () => world.Set(first, new Position(99)),
            "stale handle cannot write a component");

        var replacement = world.CreateEntity();
        Check.Equal(first.Index, replacement.Index, "world reuses released entity index");
        world.Add(replacement, new Position(1));
        Check.Equal(
            3,
            world.Query<Position>().Count(),
            "replacement has independent component membership");

        CheckStandalonePoolGenerationSafety();
    }

    private static void CheckStandalonePoolGenerationSafety()
    {
        var registry = new EntityRegistry();
        var pool = new ComponentPool<Position>(registry);
        var original = registry.Create();
        pool.Add(original, new Position(7));

        registry.Destroy(original);
        var replacement = registry.Create();
        pool.Set(replacement, new Position(8));

        Check.Equal(1, pool.Count, "pool removes stale generation at reused index");
        Check.True(!pool.TryGet(original, out _), "pool rejects stale entity generation");
        Check.Equal(8, pool.Get(replacement).X, "replacement component remains accessible");
    }

    private static void CheckDeferredCommands()
    {
        var world = new EcsWorld();
        world.RegisterComponent<Position>();
        world.RegisterComponent<Health>();
        var commands = new EcsCommandBuffer();

        var deferred = commands.CreateEntity();
        commands.Add(deferred, new Position(4));
        commands.Add(deferred, new Health(50));
        Check.Equal(3, commands.PendingCount, "deferred create records ordered commands");
        Check.Equal(0, world.EntityCount, "commands do not mutate world before playback");

        commands.Playback(world);
        var created = world.Query<Position, Health>().Single();
        Check.Equal(4, world.Get<Position>(created).X, "deferred component is applied");
        Check.Equal(50, world.Get<Health>(created).Value, "deferred components preserve order");
        Check.Equal(0, commands.PendingCount, "playback consumes recorded commands");
        Check.Throws<ArgumentException>(
            () => commands.Set(deferred, new Position(5)),
            "deferred token expires after playback");

        commands.Set(created, new Position(9));
        commands.Remove<Health>(created);
        commands.Playback(world);
        Check.Equal(9, world.Get<Position>(created).X, "set command replaces component value");
        Check.True(!world.Has<Health>(created), "remove command deletes component");

        commands.Destroy(created);
        commands.Playback(world);
        Check.True(!world.IsAlive(created), "destroy command applies at playback");
    }

    private static void CheckTypedEvents()
    {
        var events = new EcsEventBuffer();
        events.Publish(new MarkerEvent(4));
        events.Publish(new MarkerEvent(7));
        events.Publish(new DamageEvent(12));

        var markers = events.Read<MarkerEvent>();
        Check.Equal(2, markers.Length, "typed event stream count");
        Check.Equal(4, markers[0].Value, "events retain publication order");
        Check.Equal(7, markers[1].Value, "second event retains publication order");
        Check.Equal(1, events.Count<DamageEvent>(), "event types have independent streams");

        events.Clear();
        Check.Equal(0, events.Count<MarkerEvent>(), "event clear resets registered stream");
        Check.Equal(0, events.Count<DamageEvent>(), "event clear resets all event types");
    }

    private static void CheckOrderedScheduler()
    {
        var world = new EcsWorld();
        world.RegisterComponent<Position>();
        var entity = world.CreateEntity();
        var observedDeferredState = false;
        ulong observedTick = 0;
        TimeSpan observedDelta = default;

        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new DelegateSystem(20, context =>
        {
            observedDeferredState = !context.World.Has<Position>(entity);
            context.Events.Publish(new MarkerEvent(20));
        }));
        scheduler.AddSystem(new DelegateSystem(10, context =>
        {
            observedTick = context.Tick;
            observedDelta = context.DeltaTime;
            context.Commands.Add(entity, new Position(2));
            context.Events.Publish(new MarkerEvent(10));
        }));
        scheduler.AddSystem(new DelegateSystem(20, context =>
            context.Events.Publish(new MarkerEvent(21))));

        var delta = TimeSpan.FromMilliseconds(80);
        scheduler.RunTick(delta);

        Check.Equal(1UL, scheduler.CompletedTicks, "scheduler advances completed tick");
        Check.Equal(1UL, observedTick, "system context exposes current tick");
        Check.Equal(delta, observedDelta, "system context exposes fixed delta");
        Check.True(
            observedDeferredState,
            "structural commands remain deferred until all systems finish");
        Check.True(world.Has<Position>(entity), "scheduler plays commands after systems");

        var markers = scheduler.Events.Read<MarkerEvent>();
        Check.Equal(3, markers.Length, "scheduler retains completed tick events");
        Check.Equal(10, markers[0].Value, "lower system order runs first");
        Check.Equal(20, markers[1].Value, "first equal-order system retains registration order");
        Check.Equal(21, markers[2].Value, "second equal-order system remains stable");
        Check.Throws<ArgumentOutOfRangeException>(
            () => scheduler.RunTick(TimeSpan.FromTicks(-1)),
            "scheduler rejects negative delta");
    }

    private struct Position(int X)
    {
        public int X = X;
    }

    private readonly record struct Health(int Value);

    private readonly record struct MarkerEvent(int Value);

    private readonly record struct DamageEvent(int Value);

    private sealed class DelegateSystem(
        int order,
        Action<EcsSystemContext> update) : IEcsSystem
    {
        public int Order { get; } = order;

        public void Update(EcsSystemContext context) => update(context);
    }
}
