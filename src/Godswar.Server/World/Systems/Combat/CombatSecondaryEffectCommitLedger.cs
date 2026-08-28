namespace Godswar.Server.World.Systems.Combat;

internal enum CombatSecondaryEffectCommitKind : byte
{
    LifeAbsorption = 1,
    MonsterRebound = 2
}

internal readonly record struct CombatSecondaryEffectCommitKey(
    CombatSecondaryEffectCommitKind Kind,
    int CharacterId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    ulong CombatEventId)
{
    public bool IsValid =>
        Enum.IsDefined(Kind) &&
        CharacterId > 0 &&
        MonsterObjectId > 0 &&
        MonsterSpawnGeneration > 0 &&
        CombatEventId > 0;
}

/// <summary>
/// Bounded process-local replay fence for post-commit combat effects. The
/// primary damage mutation remains authoritative; this fence prevents a
/// repeated publication path from applying its secondary mutation twice.
/// </summary>
internal sealed class CombatSecondaryEffectCommitLedger
{
    public const int DefaultCapacity = 4_096;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly HashSet<CombatSecondaryEffectCommitKey> _claimed;
    private readonly HashSet<CombatSecondaryEffectCommitKey> _pending;
    private readonly Queue<CombatSecondaryEffectCommitKey> _order;

    public CombatSecondaryEffectCommitLedger(
        int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _claimed = new(capacity);
        _pending = new(capacity);
        _order = new(capacity);
    }

    public bool TryClaim(in CombatSecondaryEffectCommitKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
            if (_claimed.Contains(key) || _pending.Contains(key))
            {
                return false;
            }

            CommitLocked(key);

            return true;
        }
    }

    public bool TryReserve(in CombatSecondaryEffectCommitKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
            return !_claimed.Contains(key) && _pending.Add(key);
        }
    }

    public void Complete(in CombatSecondaryEffectCommitKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
            if (_claimed.Contains(key))
            {
                _pending.Remove(key);
                return;
            }

            _pending.Remove(key);
            CommitLocked(key);
        }
    }

    private void CommitLocked(in CombatSecondaryEffectCommitKey key)
    {
        if (_claimed.Count == _capacity)
        {
            System.Diagnostics.Debug.Assert(_order.Count > 0);
            if (_order.TryDequeue(out var evicted))
            {
                _claimed.Remove(evicted);
            }
        }
        _claimed.Add(key);
        _order.Enqueue(key);
    }

    public bool Release(in CombatSecondaryEffectCommitKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
            if (_pending.Remove(key))
            {
                return true;
            }
            if (!_claimed.Remove(key))
            {
                return false;
            }

            var released = key;
            var retained = _order
                .Where(value => value != released)
                .ToArray();
            _order.Clear();
            foreach (var value in retained)
            {
                _order.Enqueue(value);
            }

            return true;
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _claimed.Count;
            }
        }
    }
}
