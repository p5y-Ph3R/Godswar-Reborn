namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct PetHealingCooldownKey(
    int CharacterId,
    long PetId);

internal readonly record struct PetHealingCooldownReservation(
    PetHealingCooldownKey Key,
    DateTimeOffset ClaimedReadyAt,
    bool HadPrevious,
    DateTimeOffset PreviousReadyAt)
{
    public bool IsValid =>
        Key.CharacterId > 0 &&
        Key.PetId > 0 &&
        ClaimedReadyAt != default;
}

/// <summary>
/// Adapter-local rollback journal for the one optional Healing cooldown claim
/// made by an incoming-damage ECS tick.
/// </summary>
internal sealed class PetHealingCooldownTransaction
{
    private bool _active;
    private bool _hasReservation;
    private PetHealingCooldownReservation _reservation;

    public void Begin()
    {
        System.Diagnostics.Debug.Assert(!_active);
        _active = true;
        _hasReservation = false;
        _reservation = default;
    }

    public bool TryRecord(
        in PetHealingCooldownReservation reservation)
    {
        if (!_active || _hasReservation)
        {
            return false;
        }

        _reservation = reservation;
        _hasReservation = true;
        return true;
    }

    public void Commit()
    {
        _active = false;
        _hasReservation = false;
        _reservation = default;
    }

    public void RollBack(
        ProcessPetHealingCooldownStore cooldowns)
    {
        if (_active && _hasReservation)
        {
            cooldowns.RollBack(_reservation);
        }

        Commit();
    }
}

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
        return TryReserve(
            key,
            observedAt,
            cooldown,
            out readyAt,
            out _);
    }

    public bool TryReserve(
        PetHealingCooldownKey key,
        DateTimeOffset observedAt,
        TimeSpan cooldown,
        out DateTimeOffset readyAt,
        out PetHealingCooldownReservation reservation)
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
            reservation = default;
            if (_readyAtByOwnerPet.TryGetValue(
                    key,
                    out var existingReadyAt) &&
                existingReadyAt > observedAt)
            {
                readyAt = existingReadyAt;
                return false;
            }

            var hadPrevious = _readyAtByOwnerPet.TryGetValue(
                key,
                out var previousReadyAt);
            var hadEvicted = false;
            var evictedKey = default(PetHealingCooldownKey);
            if (!hadPrevious &&
                _readyAtByOwnerPet.Count >= _capacity)
            {
                foreach (var pair in _readyAtByOwnerPet)
                {
                    if (pair.Value <= observedAt)
                    {
                        hadEvicted = true;
                        evictedKey = pair.Key;
                        break;
                    }
                }

                if (!hadEvicted)
                {
                    readyAt = default;
                    return false;
                }

                _readyAtByOwnerPet.Remove(evictedKey);
            }

            readyAt = observedAt + cooldown;
            _readyAtByOwnerPet[key] = readyAt;
            reservation = new PetHealingCooldownReservation(
                key,
                readyAt,
                hadPrevious,
                previousReadyAt);
            return true;
        }
    }

    public void RollBack(
        in PetHealingCooldownReservation reservation)
    {
        if (!reservation.IsValid)
        {
            return;
        }

        lock (_gate)
        {
            if (!_readyAtByOwnerPet.TryGetValue(
                    reservation.Key,
                    out var currentReadyAt) ||
                currentReadyAt != reservation.ClaimedReadyAt)
            {
                return;
            }

            if (reservation.HadPrevious)
            {
                _readyAtByOwnerPet[reservation.Key] =
                    reservation.PreviousReadyAt;
            }
            else
            {
                _readyAtByOwnerPet.Remove(reservation.Key);
            }
        }
    }
}
