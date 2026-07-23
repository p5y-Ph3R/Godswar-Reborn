namespace Godswar.Server.Ecs;

internal interface IComponentPool
{
    int Count { get; }

    bool Remove(EntityId entity);

    void Clear();
}

/// <summary>
/// Sparse-set component storage with O(1) lookup and generation checks.
/// </summary>
internal sealed class ComponentPool<T> : IComponentPool
    where T : struct
{
    private const int InitialDenseCapacity = 4;

    private readonly EntityRegistry _registry;
    private readonly Action _onStructureChanged;
    private int[] _sparse = [];
    private EntityId[] _entities = new EntityId[InitialDenseCapacity];
    private T[] _components = new T[InitialDenseCapacity];

    public ComponentPool(
        EntityRegistry registry,
        Action? onStructureChanged = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _onStructureChanged = onStructureChanged ?? (static () => { });
    }

    public int Count { get; private set; }

    public bool Contains(EntityId entity) =>
        _registry.IsAlive(entity) && TryFindDenseIndex(entity, out _);

    public void Add(EntityId entity, in T component)
    {
        _registry.ThrowIfNotAlive(entity);
        RemoveStaleEntryAt(entity.Index);

        if (TryFindDenseIndex(entity, out _))
        {
            throw new InvalidOperationException(
                $"{entity} already has component {typeof(T).Name}.");
        }

        Append(entity, component);
    }

    /// <summary>
    /// Adds a component or replaces its value when it is already present.
    /// </summary>
    public void Set(EntityId entity, in T component)
    {
        _registry.ThrowIfNotAlive(entity);
        RemoveStaleEntryAt(entity.Index);

        if (TryFindDenseIndex(entity, out var denseIndex))
        {
            _components[denseIndex] = component;
            return;
        }

        Append(entity, component);
    }

    public ref T Get(EntityId entity)
    {
        _registry.ThrowIfNotAlive(entity);
        if (!TryFindDenseIndex(entity, out var denseIndex))
        {
            throw new KeyNotFoundException(
                $"{entity} does not have component {typeof(T).Name}.");
        }

        return ref _components[denseIndex];
    }

    public bool TryGet(EntityId entity, out T component)
    {
        if (_registry.IsAlive(entity) &&
            TryFindDenseIndex(entity, out var denseIndex))
        {
            component = _components[denseIndex];
            return true;
        }

        component = default;
        return false;
    }

    public bool Remove(EntityId entity)
    {
        if (!entity.IsValid ||
            (uint)entity.Index >= (uint)_sparse.Length ||
            !TryFindDenseIndex(entity, out var denseIndex))
        {
            return false;
        }

        RemoveAt(denseIndex);
        return true;
    }

    public void Clear()
    {
        if (Count == 0)
        {
            return;
        }

        Array.Clear(_sparse);
        Array.Clear(_entities, 0, Count);
        Array.Clear(_components, 0, Count);
        Count = 0;
        _onStructureChanged();
    }

    private void Append(EntityId entity, in T component)
    {
        EnsureSparseCapacity(entity.Index + 1);
        EnsureDenseCapacity(Count + 1);

        _entities[Count] = entity;
        _components[Count] = component;
        _sparse[entity.Index] = Count + 1;
        Count++;
        _onStructureChanged();
    }

    private void RemoveStaleEntryAt(int entityIndex)
    {
        if ((uint)entityIndex >= (uint)_sparse.Length)
        {
            return;
        }

        var denseIndex = _sparse[entityIndex] - 1;
        if ((uint)denseIndex < (uint)Count &&
            _entities[denseIndex].Index == entityIndex &&
            !_registry.IsAlive(_entities[denseIndex]))
        {
            RemoveAt(denseIndex);
        }
    }

    private bool TryFindDenseIndex(EntityId entity, out int denseIndex)
    {
        denseIndex = (uint)entity.Index < (uint)_sparse.Length
            ? _sparse[entity.Index] - 1
            : -1;

        return (uint)denseIndex < (uint)Count &&
               _entities[denseIndex] == entity;
    }

    private void RemoveAt(int denseIndex)
    {
        var removedEntity = _entities[denseIndex];
        var lastIndex = Count - 1;

        if (denseIndex != lastIndex)
        {
            var movedEntity = _entities[lastIndex];
            _entities[denseIndex] = movedEntity;
            _components[denseIndex] = _components[lastIndex];
            _sparse[movedEntity.Index] = denseIndex + 1;
        }

        _sparse[removedEntity.Index] = 0;
        _entities[lastIndex] = default;
        _components[lastIndex] = default;
        Count--;
        _onStructureChanged();
    }

    private void EnsureSparseCapacity(int required)
    {
        if (_sparse.Length >= required)
        {
            return;
        }

        var newCapacity = Math.Max(required, Math.Max(InitialDenseCapacity, _sparse.Length * 2));
        Array.Resize(ref _sparse, newCapacity);
    }

    private void EnsureDenseCapacity(int required)
    {
        if (_entities.Length >= required)
        {
            return;
        }

        var newCapacity = Math.Max(required, _entities.Length * 2);
        Array.Resize(ref _entities, newCapacity);
        Array.Resize(ref _components, newCapacity);
    }
}
