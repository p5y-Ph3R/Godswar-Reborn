namespace Godswar.Server.Ecs;

/// <summary>
/// Runs systems in stable order and plays structural commands back after all
/// systems complete.
/// </summary>
internal sealed class EcsSystemScheduler
{
    private readonly EcsWorld _world;
    private readonly EcsCommandBuffer _commands = new();
    private readonly EcsEventBuffer _events = new();
    private readonly List<SystemEntry> _systems = [];
    private long _nextRegistrationSequence;
    private bool _isUpdating;

    public EcsSystemScheduler(EcsWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public ulong CompletedTicks { get; private set; }

    public int SystemCount => _systems.Count;

    /// <summary>
    /// Events emitted by the most recently completed tick.
    /// </summary>
    public EcsEventBuffer Events => _events;

    public void AddSystem(IEcsSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        EnsureNotUpdating();

        if (_systems.Any(entry => ReferenceEquals(entry.System, system)))
        {
            throw new InvalidOperationException(
                "The same ECS system instance cannot be registered twice.");
        }

        _systems.Add(new SystemEntry(
            system,
            system.Order,
            _nextRegistrationSequence++));
        _systems.Sort(SystemEntryComparer.Instance);
    }

    public bool RemoveSystem(IEcsSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        EnsureNotUpdating();

        var index = _systems.FindIndex(
            entry => ReferenceEquals(entry.System, system));
        if (index < 0)
        {
            return false;
        }

        _systems.RemoveAt(index);
        return true;
    }

    public void RunTick(TimeSpan deltaTime)
    {
        if (deltaTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaTime),
                "Tick duration cannot be negative.");
        }

        EnsureNotUpdating();
        if (CompletedTicks == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The ECS tick counter has been exhausted.");
        }

        _commands.Discard();
        _events.Clear();
        _isUpdating = true;
        var nextTick = CompletedTicks + 1;
        var context = new EcsSystemContext(
            _world,
            nextTick,
            deltaTime,
            _commands,
            _events);

        try
        {
            foreach (var entry in _systems)
            {
                entry.System.Update(context);
            }

            _commands.Playback(_world);
            CompletedTicks = nextTick;
        }
        catch
        {
            if (_commands.PendingCount > 0)
            {
                _commands.Discard();
            }

            _events.Clear();
            throw;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void EnsureNotUpdating()
    {
        if (_isUpdating)
        {
            throw new InvalidOperationException(
                "The ECS system schedule cannot be changed during a tick.");
        }
    }

    private readonly record struct SystemEntry(
        IEcsSystem System,
        int Order,
        long RegistrationSequence);

    private sealed class SystemEntryComparer : IComparer<SystemEntry>
    {
        public static readonly SystemEntryComparer Instance = new();

        public int Compare(SystemEntry left, SystemEntry right)
        {
            var orderComparison = left.Order.CompareTo(right.Order);
            return orderComparison != 0
                ? orderComparison
                : left.RegistrationSequence.CompareTo(right.RegistrationSequence);
        }
    }
}
