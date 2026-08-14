using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

// Actor-owned transient counters and resources. Access is expected to be
// serialized by the authoritative actor lane.
internal sealed class ElementalResonanceState
{
    private const int ReplayCapacity = 512;
    private readonly long _ownerCharacterId;
    private readonly int[] _outgoingHits = new int[7];
    private readonly int[] _incomingHits = new int[7];
    private readonly int[] _thresholds = new int[7];
    private readonly long?[] _nextRecoveryAt = new long?[7];
    private readonly HashSet<ResonanceEventKey> _seen = [];
    private readonly Queue<ResonanceEventKey> _seenOrder = [];
    private long _acceptedMovementMillimeters;
    private long _momentumExpiresAtMilliseconds;
    private MomentumReservation _momentumReservation;
    private long _barrier;

    public ElementalResonanceState(long ownerCharacterId)
    {
        if (ownerCharacterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerCharacterId));
        }

        _ownerCharacterId = ownerCharacterId;
    }

    public long OwnerCharacterId => _ownerCharacterId;

    public long Barrier => _barrier;

    public long MomentumExpiresAtMilliseconds =>
        _momentumExpiresAtMilliseconds;

    public void Reconcile(ElementalEquipmentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var element in Enum.GetValues<ElementKind>())
        {
            var index = (int)element;
            var threshold = profile.HighestThresholdFor(element);
            if (_thresholds[index] == threshold)
            {
                continue;
            }

            _thresholds[index] = threshold;
            _outgoingHits[index] = 0;
            _incomingHits[index] = 0;
            _nextRecoveryAt[index] = null;
            if (element == ElementKind.Wind)
            {
                _acceptedMovementMillimeters = 0;
                _momentumExpiresAtMilliseconds = 0;
                _momentumReservation = default;
            }

            if (element == ElementKind.Light && threshold < 6)
            {
                _barrier = 0;
            }
        }
    }

    public bool TryAccept(
        DeterministicCombatEventContext combatEvent,
        ResonanceEventPhase phase)
    {
        if (!combatEvent.IsValid ||
            combatEvent.SourceCharacterId != _ownerCharacterId &&
            combatEvent.TargetCharacterId != _ownerCharacterId)
        {
            return false;
        }

        var key = new ResonanceEventKey(
            combatEvent.EventId,
            combatEvent.SourceCharacterId,
            combatEvent.TargetCharacterId,
            phase);
        if (!_seen.Add(key))
        {
            return false;
        }

        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > ReplayCapacity)
        {
            _seen.Remove(_seenOrder.Dequeue());
        }

        return true;
    }

    public int AdvanceOutgoingHit(ElementKind element) =>
        _outgoingHits[(int)element] = NextCounter(
            _outgoingHits[(int)element]);

    public int AdvanceIncomingHit(ElementKind element) =>
        _incomingHits[(int)element] = NextCounter(
            _incomingHits[(int)element]);

    public bool HasMomentum(long authoritativeTimeMilliseconds)
    {
        ExpireMomentum(authoritativeTimeMilliseconds);
        return _momentumExpiresAtMilliseconds >
            authoritativeTimeMilliseconds;
    }

    public bool ConsumeMomentum(long authoritativeTimeMilliseconds)
    {
        if (!HasMomentum(authoritativeTimeMilliseconds))
        {
            return false;
        }

        _momentumExpiresAtMilliseconds = 0;
        _momentumReservation = default;
        return true;
    }

    public bool TryReserveMomentum(
        ulong scopeId,
        ulong eventId,
        long targetCharacterId,
        long authoritativeTimeMilliseconds)
    {
        if (scopeId == 0 || eventId == 0 || targetCharacterId <= 0 ||
            !HasMomentum(authoritativeTimeMilliseconds))
        {
            return false;
        }

        if (_momentumReservation.ScopeId == scopeId)
        {
            return _momentumReservation.EventId == eventId &&
                _momentumReservation.TargetCharacterId == targetCharacterId;
        }

        // Actor-owned combat transactions are serialized. A new scope closes
        // an uncommitted prior reservation without consuming the opportunity.
        _momentumReservation = new(
            scopeId,
            eventId,
            targetCharacterId);
        return true;
    }

    public bool CommitMomentumReservation(
        ulong eventId,
        long targetCharacterId,
        long authoritativeTimeMilliseconds)
    {
        if (_momentumReservation.EventId != eventId ||
            _momentumReservation.TargetCharacterId != targetCharacterId)
        {
            return false;
        }

        return ConsumeMomentum(authoritativeTimeMilliseconds);
    }

    public ResonanceMovementResult AcceptMovement(
        long distanceMillimeters,
        long authoritativeTimeMilliseconds,
        MomentumParameters parameters)
    {
        if (distanceMillimeters < 0 || authoritativeTimeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceMillimeters));
        }

        ExpireMomentum(authoritativeTimeMilliseconds);
        if (_momentumExpiresAtMilliseconds > authoritativeTimeMilliseconds)
        {
            return new(
                distanceMillimeters,
                true,
                _momentumExpiresAtMilliseconds);
        }

        _acceptedMovementMillimeters = checked(
            _acceptedMovementMillimeters + distanceMillimeters);
        if (_acceptedMovementMillimeters >=
            parameters.AcceptedMovementMillimeters)
        {
            _acceptedMovementMillimeters = 0;
            _momentumExpiresAtMilliseconds = checked(
                authoritativeTimeMilliseconds +
                parameters.OpportunityMilliseconds);
        }

        return new(
            distanceMillimeters,
            _momentumExpiresAtMilliseconds > authoritativeTimeMilliseconds,
            _momentumExpiresAtMilliseconds);
    }

    public bool TryOpenPeriodicRecovery(
        ElementKind element,
        long authoritativeTimeMilliseconds,
        int intervalMilliseconds)
    {
        if (authoritativeTimeMilliseconds < 0 || intervalMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authoritativeTimeMilliseconds));
        }

        var index = (int)element;
        var next = _nextRecoveryAt[index];
        if (!next.HasValue)
        {
            _nextRecoveryAt[index] = checked(
                authoritativeTimeMilliseconds + intervalMilliseconds);
            return false;
        }

        if (authoritativeTimeMilliseconds < next.Value)
        {
            return false;
        }

        // At most one pulse is emitted after a delayed actor tick. Offline or
        // paused time can never produce an unbounded catch-up burst.
        _nextRecoveryAt[index] = checked(
            authoritativeTimeMilliseconds + intervalMilliseconds);
        return true;
    }

    public long AddBarrier(
        long amount,
        long maximumHealth,
        int capBasisPoints)
    {
        if (amount <= 0 || maximumHealth <= 0)
        {
            return 0;
        }

        var cap = ElementalBasisPointMath.Portion(
            maximumHealth,
            capBasisPoints);
        var before = Math.Min(_barrier, cap);
        _barrier = Math.Min(cap, checked(before + amount));
        return _barrier - before;
    }

    public long ConsumeBarrier()
    {
        var consumed = _barrier;
        _barrier = 0;
        return consumed;
    }

    public void ClearOnDeath()
    {
        Array.Clear(_outgoingHits);
        Array.Clear(_incomingHits);
        Array.Clear(_nextRecoveryAt);
        _acceptedMovementMillimeters = 0;
        _momentumExpiresAtMilliseconds = 0;
        _momentumReservation = default;
        _barrier = 0;
    }

    public void ClearOnReconnect()
    {
        ClearOnDeath();
        Array.Clear(_thresholds);
        _seen.Clear();
        _seenOrder.Clear();
    }

    private void ExpireMomentum(long authoritativeTimeMilliseconds)
    {
        if (_momentumExpiresAtMilliseconds > 0 &&
            _momentumExpiresAtMilliseconds <= authoritativeTimeMilliseconds)
        {
            _momentumExpiresAtMilliseconds = 0;
            _momentumReservation = default;
        }
    }

    // 60 is divisible by every authored hit cadence (4, 5, and 6), so bounded
    // rollover preserves trigger phase without allowing an unbounded counter.
    private static int NextCounter(int current) =>
        current >= 60 ? 1 : current + 1;

    private readonly record struct ResonanceEventKey(
        ulong EventId,
        long SourceCharacterId,
        long TargetCharacterId,
        ResonanceEventPhase Phase);

    private readonly record struct MomentumReservation(
        ulong ScopeId,
        ulong EventId,
        long TargetCharacterId);
}
