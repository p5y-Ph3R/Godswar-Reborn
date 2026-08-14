using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

// Target-owned transient state. Callers serialize access with the same actor
// authority that owns HP/movement; this type never discovers or mutates a
// target on its own.
internal sealed class ElementalStatusState
{
    private const int ReplayCapacity = 256;
    private const int MaximumPeriodicTickCount = 32;
    private readonly long _ownerCharacterId;
    private readonly Dictionary<ElementalEffectKind, ActiveEffect> _active = [];
    private readonly HashSet<StatusEventKey> _seen = [];
    private readonly Queue<StatusEventKey> _seenOrder = [];
    private IReadOnlyList<ElementalPeriodicDamageIntent>?
        _deferredBurnDamage;

    public ElementalStatusState(long ownerCharacterId)
    {
        if (ownerCharacterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerCharacterId));
        }

        _ownerCharacterId = ownerCharacterId;
    }

    public long OwnerCharacterId => _ownerCharacterId;

    public bool TryApply(ElementalEffectApplication application)
    {
        if (!IsValid(application) ||
            !Remember(new StatusEventKey(
                application.SourceCharacterId,
                application.SourceEventId,
                application.Effect,
                application.SourceProvenance)))
        {
            return false;
        }

        var incoming = new ActiveEffect(application);
        if (_active.TryGetValue(application.Effect, out var current))
        {
            if (application.Effect == ElementalEffectKind.Burn &&
                current.Application.ExpiresAtMilliseconds <=
                    application.AppliedAtMilliseconds)
            {
                if (!TryDeferExpiredBurn(
                        current,
                        application.AppliedAtMilliseconds))
                {
                    return false;
                }

                _active.Remove(ElementalEffectKind.Burn);
            }
            else if (current.Application.ExpiresAtMilliseconds >
                         application.AppliedAtMilliseconds &&
                     !IsStronger(incoming, current))
            {
                return false;
            }
        }

        _active[application.Effect] = incoming;
        return true;
    }

    public bool HasActive(
        ElementalEffectKind effect,
        long authoritativeTimeMilliseconds)
    {
        Expire(authoritativeTimeMilliseconds);
        return _active.TryGetValue(effect, out var active) &&
            active.Application.ExpiresAtMilliseconds >
                authoritativeTimeMilliseconds;
    }

    public ElementalStatusAdjustment ApplyAdjustments(
        long authoritativeTimeMilliseconds,
        long movementSpeed,
        long physicalDefense,
        long magicDefense,
        long hitRating,
        long healingReceived)
    {
        if (authoritativeTimeMilliseconds < 0 ||
            movementSpeed < 0 ||
            physicalDefense < 0 ||
            magicDefense < 0 ||
            hitRating < 0 ||
            healingReceived < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authoritativeTimeMilliseconds));
        }

        Expire(authoritativeTimeMilliseconds);
        var adjustedMovement = movementSpeed;
        if (TryPotency(ElementalEffectKind.Drench, out var drench))
        {
            adjustedMovement = ElementalBasisPointMath.ScaleDown(
                adjustedMovement,
                drench);
        }

        if (TryPotency(ElementalEffectKind.Gale, out var gale))
        {
            adjustedMovement = ElementalBasisPointMath.ScaleUp(
                adjustedMovement,
                gale);
        }

        if (TryPotency(ElementalEffectKind.Fracture, out var fracture))
        {
            physicalDefense = ElementalBasisPointMath.ScaleDown(
                physicalDefense,
                fracture);
            magicDefense = ElementalBasisPointMath.ScaleDown(
                magicDefense,
                fracture);
        }

        if (TryPotency(ElementalEffectKind.Dazzle, out var dazzle))
        {
            hitRating = ElementalBasisPointMath.ScaleDown(hitRating, dazzle);
        }

        if (TryPotency(ElementalEffectKind.Wither, out var wither))
        {
            healingReceived = ElementalBasisPointMath.ScaleDown(
                healingReceived,
                wither);
        }

        return new ElementalStatusAdjustment(
            MovementAllowed: !_active.ContainsKey(ElementalEffectKind.Shock),
            adjustedMovement,
            physicalDefense,
            magicDefense,
            hitRating,
            healingReceived);
    }

    public IReadOnlyList<ElementalPeriodicDamageIntent>
        CollectDuePeriodicDamage(long authoritativeTimeMilliseconds)
    {
        if (authoritativeTimeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authoritativeTimeMilliseconds));
        }

        if (_deferredBurnDamage is { Count: > 0 } deferred)
        {
            _deferredBurnDamage = null;
            return deferred;
        }

        if (!_active.TryGetValue(ElementalEffectKind.Burn, out var burn))
        {
            return [];
        }

        var intents = CollectDueBurnDamage(
            burn,
            authoritativeTimeMilliseconds);

        if (burn.EmittedTickCount >= burn.Application.PeriodicTickCount)
        {
            _active.Remove(ElementalEffectKind.Burn);
        }

        return intents;
    }

    public long ConsumeRemainingBurn(long authoritativeTimeMilliseconds)
    {
        if (authoritativeTimeMilliseconds < 0 ||
            !_active.Remove(ElementalEffectKind.Burn, out var burn))
        {
            return 0;
        }

        var application = burn.Application;
        return Math.Max(0, checked(
            application.PeriodicDamageTotal - burn.EmittedDamage));
    }

    public void ClearOnDeath()
    {
        _active.Clear();
        _deferredBurnDamage = null;
    }

    public void ClearOnReconnect()
    {
        _active.Clear();
        _deferredBurnDamage = null;
        _seen.Clear();
        _seenOrder.Clear();
    }

    private bool TryPotency(ElementalEffectKind effect, out int potency)
    {
        if (_active.TryGetValue(effect, out var active))
        {
            potency = active.Application.EffectivePotencyBasisPoints;
            return true;
        }

        potency = 0;
        return false;
    }

    private void Expire(long authoritativeTimeMilliseconds)
    {
        foreach (var effect in _active
                     .Where(pair =>
                         pair.Key != ElementalEffectKind.Burn &&
                         pair.Value.Application.ExpiresAtMilliseconds <=
                            authoritativeTimeMilliseconds)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _active.Remove(effect);
        }
    }

    private bool Remember(StatusEventKey key)
    {
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

    private bool IsValid(ElementalEffectApplication application) =>
        application.SourceCharacterId > 0 &&
        application.TargetCharacterId == _ownerCharacterId &&
        application.SourceEventId != 0 &&
        application.AppliedAtMilliseconds >= 0 &&
        application.ExpiresAtMilliseconds > application.AppliedAtMilliseconds &&
        application.DurationMilliseconds <= 60_000 &&
        application.EffectivePotencyBasisPoints is > 0 and <= 10_000 &&
        application.ApplicationChanceBasisPoints is > 0 and <= 10_000 &&
        application.TargetResistanceBasisPoints is >= 0 and <= 10_000 &&
        application.SourceProvenance is
            CombatEventProvenance.ElementalStatus or
            CombatEventProvenance.Resonance &&
        (application.Effect == ElementalEffectKind.Burn
            ? application.PeriodicDamageTotal > 0 &&
              application.PeriodicTickCount is
                  > 0 and <= MaximumPeriodicTickCount
            : application.PeriodicDamageTotal == 0 &&
              application.PeriodicTickCount == 0);

    private bool TryDeferExpiredBurn(
        ActiveEffect burn,
        long authoritativeTimeMilliseconds)
    {
        if (_deferredBurnDamage is { Count: > 0 })
        {
            // Keep the expired active Burn intact until the target-owner loop
            // drains the already deferred batch. This bounds pending state to
            // one authored batch without discarding any committed damage.
            return false;
        }

        var due = CollectDueBurnDamage(
            burn,
            authoritativeTimeMilliseconds);
        if (due.Count > 0)
        {
            _deferredBurnDamage = due;
        }

        return true;
    }

    private static IReadOnlyList<ElementalPeriodicDamageIntent>
        CollectDueBurnDamage(
            ActiveEffect burn,
            long authoritativeTimeMilliseconds)
    {
        var application = burn.Application;
        var intents = new List<ElementalPeriodicDamageIntent>();
        while (burn.EmittedTickCount < application.PeriodicTickCount)
        {
            var ordinal = burn.EmittedTickCount + 1;
            var dueAt = application.AppliedAtMilliseconds + checked(
                ((long)application.DurationMilliseconds * ordinal) /
                application.PeriodicTickCount);
            if (dueAt > authoritativeTimeMilliseconds)
            {
                break;
            }

            var amount = DamageForTick(application, ordinal);
            burn.EmittedTickCount = ordinal;
            burn.EmittedDamage = checked(burn.EmittedDamage + amount);
            intents.Add(new ElementalPeriodicDamageIntent(
                application.Element,
                application.Effect,
                application.SourceCharacterId,
                application.TargetCharacterId,
                application.SourceEventId,
                ordinal,
                amount,
                CombatEventProvenance.ElementalStatus));
        }

        return intents.AsReadOnly();
    }

    private static bool IsStronger(ActiveEffect incoming, ActiveEffect current)
    {
        if (incoming.Application.Effect == ElementalEffectKind.Burn)
        {
            var incomingDamage = incoming.Application.PeriodicDamageTotal;
            var currentRemaining = checked(
                current.Application.PeriodicDamageTotal - current.EmittedDamage);
            return incomingDamage > currentRemaining ||
                incomingDamage == currentRemaining &&
                incoming.Application.ExpiresAtMilliseconds >
                    current.Application.ExpiresAtMilliseconds;
        }

        return incoming.Application.EffectivePotencyBasisPoints >
                current.Application.EffectivePotencyBasisPoints ||
            incoming.Application.EffectivePotencyBasisPoints ==
                current.Application.EffectivePotencyBasisPoints &&
            incoming.Application.ExpiresAtMilliseconds >
                current.Application.ExpiresAtMilliseconds;
    }

    private static long DamageForTick(
        ElementalEffectApplication application,
        int ordinal)
    {
        var baseDamage = application.PeriodicDamageTotal /
            application.PeriodicTickCount;
        var remainder = application.PeriodicDamageTotal %
            application.PeriodicTickCount;
        return checked(baseDamage + (ordinal <= remainder ? 1 : 0));
    }

    private readonly record struct StatusEventKey(
        long SourceCharacterId,
        ulong SourceEventId,
        ElementalEffectKind Effect,
        CombatEventProvenance Provenance);

    private sealed class ActiveEffect(
        ElementalEffectApplication application)
    {
        public ElementalEffectApplication Application { get; } = application;

        public int EmittedTickCount { get; set; }

        public long EmittedDamage { get; set; }
    }
}
