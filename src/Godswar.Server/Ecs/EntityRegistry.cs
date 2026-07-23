namespace Godswar.Server.Ecs;

/// <summary>
/// Owns entity indices and generations. Destroyed indices are reused in
/// deterministic last-released-first order.
/// </summary>
internal sealed class EntityRegistry
{
    private readonly List<Slot> _slots = [];
    private readonly Stack<int> _freeIndices = [];
    private int _version;

    public int Count { get; private set; }

    public int Capacity => _slots.Count;

    public int Version => _version;

    public EntityId Create()
    {
        int index;
        Slot slot;

        if (_freeIndices.TryPop(out index))
        {
            slot = _slots[index];
            if (slot.IsAlive || slot.Generation is 0 or uint.MaxValue)
            {
                throw new InvalidOperationException(
                    "The entity free list is internally inconsistent.");
            }

            slot.IsAlive = true;
            _slots[index] = slot;
        }
        else
        {
            index = _slots.Count;
            slot = new Slot(Generation: 1, IsAlive: true);
            _slots.Add(slot);
        }

        Count++;
        _version++;
        return EntityId.FromParts(index, slot.Generation);
    }

    public bool IsAlive(EntityId entity)
    {
        if (!entity.IsValid || (uint)entity.Index >= (uint)_slots.Count)
        {
            return false;
        }

        var slot = _slots[entity.Index];
        return slot.IsAlive && slot.Generation == entity.Generation;
    }

    public void Destroy(EntityId entity)
    {
        if (!TryDestroy(entity))
        {
            throw new InvalidOperationException(
                $"{entity} is stale or is not alive.");
        }
    }

    public bool TryDestroy(EntityId entity)
    {
        if (!IsAlive(entity))
        {
            return false;
        }

        var index = entity.Index;
        var slot = _slots[index];
        slot.IsAlive = false;

        // Retire an index instead of wrapping its generation and allowing an
        // ancient handle to become valid again.
        if (slot.Generation != uint.MaxValue)
        {
            slot.Generation++;
            _freeIndices.Push(index);
        }

        _slots[index] = slot;
        Count--;
        _version++;
        return true;
    }

    public void ThrowIfNotAlive(EntityId entity)
    {
        if (!IsAlive(entity))
        {
            throw new InvalidOperationException(
                $"{entity} is stale or is not alive.");
        }
    }

    /// <summary>
    /// Enumerates live entities by ascending index. Structural entity changes
    /// during enumeration fail fast.
    /// </summary>
    public IEnumerable<EntityId> EnumerateAlive()
    {
        var expectedVersion = _version;

        for (var index = 0; index < _slots.Count; index++)
        {
            EnsureUnchanged(expectedVersion);
            var slot = _slots[index];
            if (slot.IsAlive)
            {
                yield return EntityId.FromParts(index, slot.Generation);
            }
        }

        EnsureUnchanged(expectedVersion);
    }

    private void EnsureUnchanged(int expectedVersion)
    {
        if (_version != expectedVersion)
        {
            throw new InvalidOperationException(
                "Entity structure changed during enumeration. Queue structural " +
                "changes through EcsCommandBuffer.");
        }
    }

    private struct Slot(uint Generation, bool IsAlive)
    {
        public uint Generation = Generation;
        public bool IsAlive = IsAlive;
    }
}
