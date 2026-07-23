namespace Godswar.Server.Ecs;

/// <summary>
/// A single-writer ECS world. Entity and component structural changes must be
/// made on the world's owning simulation thread.
/// </summary>
internal sealed class EcsWorld
{
    private readonly EntityRegistry _entities = new();
    private readonly List<IComponentPool> _registeredPools = [];
    private IComponentPool?[] _poolsByType = new IComponentPool[8];
    private int _structuralVersion;

    public int EntityCount => _entities.Count;

    public int EntityCapacity => _entities.Capacity;

    public int RegisteredComponentCount => _registeredPools.Count;

    public EntityId CreateEntity()
    {
        var entity = _entities.Create();
        _structuralVersion++;
        return entity;
    }

    public bool IsAlive(EntityId entity) => _entities.IsAlive(entity);

    public void DestroyEntity(EntityId entity)
    {
        if (!TryDestroyEntity(entity))
        {
            throw new InvalidOperationException(
                $"{entity} is stale or is not alive.");
        }
    }

    public bool TryDestroyEntity(EntityId entity)
    {
        if (!_entities.IsAlive(entity))
        {
            return false;
        }

        foreach (var pool in _registeredPools)
        {
            pool.Remove(entity);
        }

        _entities.Destroy(entity);
        _structuralVersion++;
        return true;
    }

    public ComponentPool<T> RegisterComponent<T>()
        where T : struct
    {
        var typeId = EcsComponentType<T>.Id;
        EnsurePoolCapacity(typeId + 1);

        if (_poolsByType[typeId] is ComponentPool<T> existing)
        {
            return existing;
        }

        if (_poolsByType[typeId] is not null)
        {
            throw new InvalidOperationException(
                "Two ECS component types were assigned the same internal ID.");
        }

        var pool = new ComponentPool<T>(
            _entities,
            () => _structuralVersion++);
        _poolsByType[typeId] = pool;
        _registeredPools.Add(pool);
        _structuralVersion++;
        return pool;
    }

    public bool IsComponentRegistered<T>()
        where T : struct
    {
        var typeId = EcsComponentType<T>.Id;
        return (uint)typeId < (uint)_poolsByType.Length &&
               _poolsByType[typeId] is ComponentPool<T>;
    }

    public ComponentPool<T> GetPool<T>()
        where T : struct
    {
        var typeId = EcsComponentType<T>.Id;
        if ((uint)typeId < (uint)_poolsByType.Length &&
            _poolsByType[typeId] is ComponentPool<T> pool)
        {
            return pool;
        }

        throw new InvalidOperationException(
            $"Component {typeof(T).Name} has not been registered.");
    }

    public void Add<T>(EntityId entity, in T component)
        where T : struct =>
        GetPool<T>().Add(entity, component);

    public void Set<T>(EntityId entity, in T component)
        where T : struct =>
        GetPool<T>().Set(entity, component);

    public ref T Get<T>(EntityId entity)
        where T : struct =>
        ref GetPool<T>().Get(entity);

    public bool TryGet<T>(EntityId entity, out T component)
        where T : struct =>
        GetPool<T>().TryGet(entity, out component);

    public bool Has<T>(EntityId entity)
        where T : struct =>
        GetPool<T>().Contains(entity);

    public bool Remove<T>(EntityId entity)
        where T : struct =>
        GetPool<T>().Remove(entity);

    public IEnumerable<EntityId> EnumerateEntities()
    {
        var expectedVersion = _structuralVersion;
        foreach (var entity in _entities.EnumerateAlive())
        {
            EnsureStructureUnchanged(expectedVersion);
            yield return entity;
        }

        EnsureStructureUnchanged(expectedVersion);
    }

    public IEnumerable<EntityId> Query<T>()
        where T : struct
    {
        var pool = GetPool<T>();
        var expectedVersion = _structuralVersion;

        foreach (var entity in _entities.EnumerateAlive())
        {
            EnsureStructureUnchanged(expectedVersion);
            if (pool.Contains(entity))
            {
                yield return entity;
            }
        }

        EnsureStructureUnchanged(expectedVersion);
    }

    public IEnumerable<EntityId> Query<TFirst, TSecond>()
        where TFirst : struct
        where TSecond : struct
    {
        var first = GetPool<TFirst>();
        var second = GetPool<TSecond>();
        var expectedVersion = _structuralVersion;

        foreach (var entity in _entities.EnumerateAlive())
        {
            EnsureStructureUnchanged(expectedVersion);
            if (first.Contains(entity) && second.Contains(entity))
            {
                yield return entity;
            }
        }

        EnsureStructureUnchanged(expectedVersion);
    }

    public IEnumerable<EntityId> Query<TFirst, TSecond, TThird>()
        where TFirst : struct
        where TSecond : struct
        where TThird : struct
    {
        var first = GetPool<TFirst>();
        var second = GetPool<TSecond>();
        var third = GetPool<TThird>();
        var expectedVersion = _structuralVersion;

        foreach (var entity in _entities.EnumerateAlive())
        {
            EnsureStructureUnchanged(expectedVersion);
            if (first.Contains(entity) &&
                second.Contains(entity) &&
                third.Contains(entity))
            {
                yield return entity;
            }
        }

        EnsureStructureUnchanged(expectedVersion);
    }

    private void EnsureStructureUnchanged(int expectedVersion)
    {
        if (_structuralVersion != expectedVersion)
        {
            throw new InvalidOperationException(
                "World structure changed during a query. Queue structural " +
                "changes through EcsCommandBuffer.");
        }
    }

    private void EnsurePoolCapacity(int required)
    {
        if (_poolsByType.Length >= required)
        {
            return;
        }

        var newCapacity = Math.Max(required, _poolsByType.Length * 2);
        Array.Resize(ref _poolsByType, newCapacity);
    }
}
