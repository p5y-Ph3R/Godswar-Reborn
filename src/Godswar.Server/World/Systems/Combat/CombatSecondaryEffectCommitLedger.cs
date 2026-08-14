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
    private readonly HashSet<CombatSecondaryEffectCommitKey> _claimed = [];
    private readonly Queue<CombatSecondaryEffectCommitKey> _order = [];

    public CombatSecondaryEffectCommitLedger(
        int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public bool TryClaim(in CombatSecondaryEffectCommitKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
            if (!_claimed.Add(key))
            {
                return false;
            }

            _order.Enqueue(key);
            while (_order.Count > _capacity)
            {
                _claimed.Remove(_order.Dequeue());
            }

            return true;
        }
    }

    public bool Release(in CombatSecondaryEffectCommitKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
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
