namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct PetHealingCooldownKey(
    int CharacterId,
    long PetId);

/// <summary>
/// Process-lifetime Healing ledger. It is shared by all session adapters in
/// one game-server runtime, so disconnecting and reconnecting cannot clear a
/// cooldown. Entries are strictly bounded and expired entries are reclaimed
/// before capacity can be exceeded.
/// </summary>
internal sealed class ProcessPetHealingCooldownStore
{
    public const int DefaultCapacity = 65_536;

    private readonly object _gate = new();
    private readonly Dictionary<
        PetHealingCooldownKey,
        DateTimeOffset> _readyAtByOwnerPet = [];
    private readonly int _capacity;

    public ProcessPetHealingCooldownStore(
        int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _readyAtByOwnerPet.Count;
            }
        }
    }

    public bool TryClaim(
        PetHealingCooldownKey key,
        DateTimeOffset observedAt,
        TimeSpan cooldown,
        out DateTimeOffset readyAt)
    {
        if (key.CharacterId <= 0 || key.PetId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
        if (observedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(observedAt));
        }
        if (cooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        lock (_gate)
        {
            if (_readyAtByOwnerPet.TryGetValue(
                    key,
                    out var existingReadyAt) &&
                existingReadyAt > observedAt)
            {
                readyAt = existingReadyAt;
                return false;
            }

            if (!_readyAtByOwnerPet.ContainsKey(key) &&
                _readyAtByOwnerPet.Count >= _capacity)
            {
                ReclaimExpired(observedAt);
                if (_readyAtByOwnerPet.Count >= _capacity)
                {
                    readyAt = default;
                    return false;
                }
            }

            readyAt = observedAt + cooldown;
            _readyAtByOwnerPet[key] = readyAt;
            return true;
        }
    }

    private void ReclaimExpired(DateTimeOffset observedAt)
    {
        foreach (var key in _readyAtByOwnerPet
                     .Where(pair => pair.Value <= observedAt)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _readyAtByOwnerPet.Remove(key);
        }
    }
}
