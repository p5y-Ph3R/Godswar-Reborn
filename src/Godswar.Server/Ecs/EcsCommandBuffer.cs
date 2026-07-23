using System.Threading;

namespace Godswar.Server.Ecs;

/// <summary>
/// Records structural changes and applies them later in exact recording order.
/// </summary>
internal sealed class EcsCommandBuffer
{
    private static long _nextOwnerId;

    private readonly long _ownerId = Interlocked.Increment(ref _nextOwnerId);
    private readonly List<IEcsCommand> _commands = [];
    private uint _epoch = 1;
    private int _deferredEntityCount;
    private bool _isPlayingBack;

    public int PendingCount => _commands.Count;

    public EcsDeferredEntity CreateEntity()
    {
        EnsureRecording();
        var token = new EcsDeferredEntity(
            _ownerId,
            _epoch,
            _deferredEntityCount++);
        _commands.Add(new CreateEntityCommand(token.Ordinal));
        return token;
    }

    public void Destroy(EntityId entity) =>
        Record(new DestroyEntityCommand(ToTarget(entity)));

    public void Destroy(EcsDeferredEntity entity) =>
        Record(new DestroyEntityCommand(ToTarget(entity)));

    public void Add<T>(EntityId entity, in T component)
        where T : struct =>
        Record(new WriteComponentCommand<T>(
            ToTarget(entity),
            component,
            ComponentWriteMode.Add));

    public void Add<T>(EcsDeferredEntity entity, in T component)
        where T : struct =>
        Record(new WriteComponentCommand<T>(
            ToTarget(entity),
            component,
            ComponentWriteMode.Add));

    public void Set<T>(EntityId entity, in T component)
        where T : struct =>
        Record(new WriteComponentCommand<T>(
            ToTarget(entity),
            component,
            ComponentWriteMode.Set));

    public void Set<T>(EcsDeferredEntity entity, in T component)
        where T : struct =>
        Record(new WriteComponentCommand<T>(
            ToTarget(entity),
            component,
            ComponentWriteMode.Set));

    public void Remove<T>(EntityId entity)
        where T : struct =>
        Record(new RemoveComponentCommand<T>(ToTarget(entity)));

    public void Remove<T>(EcsDeferredEntity entity)
        where T : struct =>
        Record(new RemoveComponentCommand<T>(ToTarget(entity)));

    public void Playback(EcsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsureRecording();
        _isPlayingBack = true;

        var createdEntities = _deferredEntityCount == 0
            ? []
            : new EntityId[_deferredEntityCount];

        try
        {
            foreach (var command in _commands)
            {
                command.Apply(world, createdEntities);
            }
        }
        finally
        {
            _isPlayingBack = false;
            Reset();
        }
    }

    public void Discard()
    {
        EnsureRecording();
        Reset();
    }

    private void Record(IEcsCommand command)
    {
        EnsureRecording();
        _commands.Add(command);
    }

    private CommandEntityTarget ToTarget(EntityId entity)
    {
        if (!entity.IsValid)
        {
            throw new ArgumentException(
                "A command target must be a non-null entity.",
                nameof(entity));
        }

        return CommandEntityTarget.ForExisting(entity);
    }

    private CommandEntityTarget ToTarget(EcsDeferredEntity entity)
    {
        if (!entity.IsValid ||
            entity.OwnerId != _ownerId ||
            entity.Epoch != _epoch ||
            entity.Ordinal >= _deferredEntityCount)
        {
            throw new ArgumentException(
                "The deferred entity does not belong to this recording.",
                nameof(entity));
        }

        return CommandEntityTarget.ForDeferred(entity.Ordinal);
    }

    private void Reset()
    {
        _commands.Clear();
        _deferredEntityCount = 0;
        _epoch = _epoch == uint.MaxValue ? 1 : _epoch + 1;
    }

    private void EnsureRecording()
    {
        if (_isPlayingBack)
        {
            throw new InvalidOperationException(
                "The command buffer cannot be modified during playback.");
        }
    }

    private interface IEcsCommand
    {
        void Apply(EcsWorld world, EntityId[] createdEntities);
    }

    private readonly record struct CommandEntityTarget(
        EntityId Existing,
        int DeferredOrdinal)
    {
        public bool IsDeferred => DeferredOrdinal >= 0;

        public static CommandEntityTarget ForExisting(EntityId entity) =>
            new(entity, -1);

        public static CommandEntityTarget ForDeferred(int ordinal) =>
            new(EntityId.None, ordinal);

        public EntityId Resolve(EntityId[] createdEntities)
        {
            if (!IsDeferred)
            {
                return Existing;
            }

            var entity = createdEntities[DeferredOrdinal];
            if (!entity.IsValid)
            {
                throw new InvalidOperationException(
                    "A deferred entity was used before its create command.");
            }

            return entity;
        }
    }

    private sealed class CreateEntityCommand(int ordinal) : IEcsCommand
    {
        public void Apply(EcsWorld world, EntityId[] createdEntities)
        {
            if (createdEntities[ordinal].IsValid)
            {
                throw new InvalidOperationException(
                    "A deferred entity was created more than once.");
            }

            createdEntities[ordinal] = world.CreateEntity();
        }
    }

    private sealed class DestroyEntityCommand(
        CommandEntityTarget target) : IEcsCommand
    {
        public void Apply(EcsWorld world, EntityId[] createdEntities) =>
            world.DestroyEntity(target.Resolve(createdEntities));
    }

    private sealed class WriteComponentCommand<T>(
        CommandEntityTarget target,
        T component,
        ComponentWriteMode mode) : IEcsCommand
        where T : struct
    {
        public void Apply(EcsWorld world, EntityId[] createdEntities)
        {
            var entity = target.Resolve(createdEntities);
            if (mode == ComponentWriteMode.Add)
            {
                world.Add(entity, component);
            }
            else
            {
                world.Set(entity, component);
            }
        }
    }

    private sealed class RemoveComponentCommand<T>(
        CommandEntityTarget target) : IEcsCommand
        where T : struct
    {
        public void Apply(EcsWorld world, EntityId[] createdEntities) =>
            world.Remove<T>(target.Resolve(createdEntities));
    }

    private enum ComponentWriteMode : byte
    {
        Add,
        Set
    }
}
