namespace Godswar.Server.State;

internal enum HostileStatusTargetMode : byte
{
    SingleTarget = 1,
    SelfCenteredArea = 2
}

internal enum HostileStatusApplicationTrigger : byte
{
    CommittedCast = 1,
    CommittedDamagingHit = 2
}

[Flags]
internal enum HostileStatusControlFlags : ushort
{
    None = 0,
    HaltIntonate = 1 << 0,
    NonMoving = 1 << 1,
    NonMagicUsing = 1 << 2,
    NonTechniqueUsing = 1 << 3,
    NonAttackUsing = 1 << 4,
    NonItemUsing = 1 << 5
}

/// <summary>
/// Stock hostile-status content. Native StatusOdds is retained as an audit
/// rating only; the original conversion from that rating is not recovered.
/// Runtime percentages use basis points (10,000 = 100%).
/// </summary>
internal readonly record struct HostileStatusEffectDefinition(
    int SkillId,
    byte RequiredProfession,
    uint StatusId,
    int NativeStatusOddsRating,
    int Kind,
    int Priority,
    TimeSpan Duration,
    TimeSpan Cooldown,
    int ManaCost,
    HostileStatusTargetMode TargetMode,
    float Range,
    HostileStatusApplicationTrigger Trigger,
    int PhysicalDefenseModifier = 0,
    int MagicDefenseModifier = 0,
    int PhysicalDamageTakenIncreaseBasisPoints = 0,
    int MagicDamageTakenIncreaseBasisPoints = 0,
    int PhysicalDamageReductionBasisPoints = 0,
    int MagicDamageReductionBasisPoints = 0,
    HostileStatusControlFlags Control = HostileStatusControlFlags.None)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SkillId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StatusId);
        ArgumentOutOfRangeException.ThrowIfNegative(NativeStatusOddsRating);
        ArgumentOutOfRangeException.ThrowIfNegative(Kind);
        ArgumentOutOfRangeException.ThrowIfNegative(Priority);
        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Duration));
        }
        if (Cooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Cooldown));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(ManaCost);
        if (!Enum.IsDefined(TargetMode))
        {
            throw new ArgumentOutOfRangeException(nameof(TargetMode));
        }
        if (!float.IsFinite(Range) || Range < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(Range));
        }
        if (!Enum.IsDefined(Trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(Trigger));
        }
        ValidateBasisPoints(
            PhysicalDamageTakenIncreaseBasisPoints,
            nameof(PhysicalDamageTakenIncreaseBasisPoints));
        ValidateBasisPoints(
            MagicDamageTakenIncreaseBasisPoints,
            nameof(MagicDamageTakenIncreaseBasisPoints));
        ValidateBasisPoints(
            PhysicalDamageReductionBasisPoints,
            nameof(PhysicalDamageReductionBasisPoints));
        ValidateBasisPoints(
            MagicDamageReductionBasisPoints,
            nameof(MagicDamageReductionBasisPoints));
        if ((Control & ~AllControlFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Control));
        }
    }

    private const HostileStatusControlFlags AllControlFlags =
        HostileStatusControlFlags.HaltIntonate |
        HostileStatusControlFlags.NonMoving |
        HostileStatusControlFlags.NonMagicUsing |
        HostileStatusControlFlags.NonTechniqueUsing |
        HostileStatusControlFlags.NonAttackUsing |
        HostileStatusControlFlags.NonItemUsing;

    private static void ValidateBasisPoints(int value, string name)
    {
        if (value is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
