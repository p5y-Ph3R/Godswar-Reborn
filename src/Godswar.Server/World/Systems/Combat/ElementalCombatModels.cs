using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal enum CombatEventProvenance : byte
{
    DirectBasicAttack,
    DirectSkill,
    ElementalStatus,
    Resonance,
    Reflection,
    Recovery,
    AcceptedMovement,
    CreditedKill
}

internal readonly record struct DeterministicCombatEventContext(
    ulong EventId,
    int MapId,
    long SourceCharacterId,
    long TargetCharacterId,
    long AuthoritativeTimeMilliseconds,
    CombatEventProvenance Provenance,
    bool Committed,
    bool IsPvp,
    PvpEligibilityResult PvpEligibility)
{
    public bool IsValid =>
        EventId != 0 &&
        MapId >= 0 &&
        SourceCharacterId > 0 &&
        TargetCharacterId > 0 &&
        (Provenance is CombatEventProvenance.AcceptedMovement or
            CombatEventProvenance.Recovery
            ? SourceCharacterId == TargetCharacterId
            : !IsPvp || SourceCharacterId != TargetCharacterId) &&
        AuthoritativeTimeMilliseconds >= 0 &&
        (!IsPvp ||
            Provenance is CombatEventProvenance.AcceptedMovement or
                CombatEventProvenance.Recovery ||
            PvpEligibility.Admits(
            SourceCharacterId,
            TargetCharacterId,
            MapId));

    public bool IsDirectAttempt =>
        IsValid &&
        Provenance is CombatEventProvenance.DirectBasicAttack or
            CombatEventProvenance.DirectSkill;

    public bool IsCommittedDirectHit =>
        IsDirectAttempt && Committed;

    public bool IsAcceptedMovement =>
        IsValid &&
        Committed &&
        Provenance == CombatEventProvenance.AcceptedMovement &&
        SourceCharacterId == TargetCharacterId;
}

internal readonly record struct ElementalExecutionLimits(
    int MaximumPotencyBasisPoints,
    int MaximumResistanceBasisPoints,
    int MaximumApplicationChanceBasisPoints,
    int MaximumStatusDurationMilliseconds,
    int MaximumTriggeredDamageBasisPointsOfAppliedHit,
    int MaximumReflectionBasisPointsOfAttackerMaximumHealth,
    int MaximumResourceEffectBasisPointsOfMaximum)
{
    public const int BasisPointDenominator = 10_000;

    public static ElementalExecutionLimits CurrentPve { get; } = new(
        MaximumPotencyBasisPoints: 1_000,
        MaximumResistanceBasisPoints: 7_000,
        MaximumApplicationChanceBasisPoints: 2_000,
        MaximumStatusDurationMilliseconds: 60_000,
        MaximumTriggeredDamageBasisPointsOfAppliedHit: 1_500,
        MaximumReflectionBasisPointsOfAttackerMaximumHealth: 200,
        MaximumResourceEffectBasisPointsOfMaximum: 1_000);

    public static ElementalExecutionLimits FromPvp(PvpCombatCaps caps)
    {
        if (!caps.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(caps),
                "PvP combat caps must be valid.");
        }

        return new(
            caps.MaximumElementalPotencyBasisPoints,
            caps.MaximumElementalResistanceBasisPoints,
            caps.MaximumElementalApplicationChanceBasisPoints,
            caps.MaximumElementalStatusDurationMilliseconds,
            caps.MaximumTriggeredDamageBasisPointsOfAppliedHit,
            caps.MaximumReflectionBasisPointsOfAttackerMaximumHealth,
            caps.MaximumResourceEffectBasisPointsOfMaximum);
    }

    public bool IsValid =>
        IsBasisPointValue(MaximumPotencyBasisPoints) &&
        IsBasisPointValue(MaximumResistanceBasisPoints) &&
        IsBasisPointValue(MaximumApplicationChanceBasisPoints) &&
        MaximumStatusDurationMilliseconds is > 0 and <= 60_000 &&
        IsBasisPointValue(MaximumTriggeredDamageBasisPointsOfAppliedHit) &&
        IsBasisPointValue(
            MaximumReflectionBasisPointsOfAttackerMaximumHealth) &&
        IsBasisPointValue(MaximumResourceEffectBasisPointsOfMaximum);

    private static bool IsBasisPointValue(int value) =>
        value is >= 0 and <= BasisPointDenominator;
}

internal readonly record struct ElementalEffectExecutionTuning(
    int BurnDurationMilliseconds,
    int BurnTickCount,
    int DrenchDurationMilliseconds,
    int ShockMaximumDurationMilliseconds,
    int FractureDurationMilliseconds,
    int GaleDurationMilliseconds,
    int DazzleDurationMilliseconds,
    int WitherDurationMilliseconds)
{
    public int DurationFor(ElementalEffectKind effect) =>
        effect switch
        {
            ElementalEffectKind.Burn => BurnDurationMilliseconds,
            ElementalEffectKind.Drench => DrenchDurationMilliseconds,
            ElementalEffectKind.Shock => ShockMaximumDurationMilliseconds,
            ElementalEffectKind.Fracture => FractureDurationMilliseconds,
            ElementalEffectKind.Gale => GaleDurationMilliseconds,
            ElementalEffectKind.Dazzle => DazzleDurationMilliseconds,
            ElementalEffectKind.Wither => WitherDurationMilliseconds,
            _ => throw new ArgumentOutOfRangeException(nameof(effect))
        };

    public bool IsValid(ElementalExecutionLimits limits)
    {
        if (!limits.IsValid || BurnTickCount is < 1 or > 32)
        {
            return false;
        }

        foreach (var effect in Enum.GetValues<ElementalEffectKind>())
        {
            var duration = DurationFor(effect);
            if (duration <= 0 ||
                duration > limits.MaximumStatusDurationMilliseconds)
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly record struct ElementalEffectApplication(
    ElementKind Element,
    ElementalEffectKind Effect,
    long SourceCharacterId,
    long TargetCharacterId,
    ulong SourceEventId,
    long AppliedAtMilliseconds,
    long ExpiresAtMilliseconds,
    int EffectivePotencyBasisPoints,
    int ApplicationChanceBasisPoints,
    int TargetResistanceBasisPoints,
    long PeriodicDamageTotal,
    int PeriodicTickCount,
    CombatEventProvenance SourceProvenance)
{
    public int DurationMilliseconds => checked(
        (int)(ExpiresAtMilliseconds - AppliedAtMilliseconds));
}

internal readonly record struct ElementalPeriodicDamageIntent(
    ElementKind Element,
    ElementalEffectKind Effect,
    long SourceCharacterId,
    long TargetCharacterId,
    ulong SourceEventId,
    int TickOrdinal,
    long Damage,
    CombatEventProvenance Provenance);

internal readonly record struct ElementalStatusAdjustment(
    bool MovementAllowed,
    long MovementSpeed,
    long PhysicalDefense,
    long MagicDefense,
    long HitRating,
    long HealingReceived);

internal static class ElementalBasisPointMath
{
    public const int Denominator = 10_000;

    public static int ClampBasisPoints(int value, int maximum) =>
        Math.Clamp(value, 0, Math.Clamp(maximum, 0, Denominator));

    public static long ScaleDown(long value, int reductionBasisPoints)
    {
        if (value <= 0)
        {
            return 0;
        }

        var reduction = ClampBasisPoints(
            reductionBasisPoints,
            Denominator);
        return checked((long)(((decimal)value *
            (Denominator - reduction)) / Denominator));
    }

    public static long ScaleUp(long value, int bonusBasisPoints)
    {
        if (value <= 0)
        {
            return 0;
        }

        var bonus = ClampBasisPoints(bonusBasisPoints, Denominator);
        return checked((long)(((decimal)value *
            (Denominator + bonus)) / Denominator));
    }

    public static long Portion(long value, int basisPoints)
    {
        if (value <= 0)
        {
            return 0;
        }

        var portion = ClampBasisPoints(basisPoints, Denominator);
        return checked((long)(((decimal)value * portion) / Denominator));
    }
}
