namespace Godswar.Server.World.Systems.Combat;

internal enum CombatDamageChannel : byte
{
    Physical = 1,
    Magic = 2
}

internal enum CombatHitOutcome : byte
{
    // Values match the captured opcode-10026 outcome byte.
    Critical = 0,
    Normal = 1,
    Miss = 2
}

/// <summary>
/// Immutable offensive snapshot. Every percentage-like value is expressed in
/// basis points (10,000 = 100%); append and flat values use damage units.
/// </summary>
internal readonly record struct CombatAttackerStats
{
    public int Level { get; init; }
    public byte Profession { get; init; }
    public int PhysicalAttack { get; init; }
    public int MagicAttack { get; init; }
    public int Hit { get; init; }
    public int Critical { get; init; }
    public int PhysicalDamageBonusBasisPoints { get; init; }
    public int MagicDamageBonusBasisPoints { get; init; }
    public int PhysicalAppendDamage { get; init; }
    public int MagicAppendDamage { get; init; }
    public int IgnorePhysicalDefenseBasisPoints { get; init; }
    public int IgnoreMagicDefenseBasisPoints { get; init; }
    public int CriticalDamageBasisPoints { get; init; }
    public int CriticalDamageFlat { get; init; }
    public int LifeAbsorptionBasisPoints { get; init; }
}

/// <summary>
/// Target-side combat seam. Monster content may populate this without exposing
/// its persistence model to combat. Percentage-like values are basis points;
/// absorption and flat reduction values are damage units.
/// </summary>
internal readonly record struct CombatTargetStats
{
    public int Level { get; init; }
    public int PhysicalDefense { get; init; }
    public int MagicDefense { get; init; }
    public int Dodge { get; init; }
    public int CriticalResistance { get; init; }
    public int PhysicalDamageReductionBasisPoints { get; init; }
    public int MagicDamageReductionBasisPoints { get; init; }
    public int CriticalDamageReductionBasisPoints { get; init; }
    public int PhysicalFlatAbsorption { get; init; }
    public int MagicFlatAbsorption { get; init; }
    public int CriticalDamageFlatReduction { get; init; }
    public int DamageReboundBasisPoints { get; init; }
    public int DamageReboundFlat { get; init; }
}

internal readonly record struct CombatRollEvidence(
    int HitChanceBasisPoints,
    int HitRollBasisPoints,
    int CriticalChanceBasisPoints,
    int CriticalRollBasisPoints)
{
    public const int NotRolled = -1;
}

internal readonly record struct CombatDamageEvidence(
    int Attack,
    int EffectiveDefense,
    decimal AttackAfterDefense,
    decimal SkillCoreDamage,
    decimal DamageAfterTypedBonus,
    decimal CriticalBonusDamage,
    decimal DamageWithAppend,
    decimal DamageAfterReduction,
    decimal DamageAfterAbsorption);

/// <summary>
/// Replayable normal/miss/critical decision. EventId and TargetOrder identify
/// the exact deterministic rolls that produced the result.
/// </summary>
internal readonly record struct CombatResolution(
    int FormulaVersion,
    ulong EventId,
    int TargetOrder,
    CombatDamageChannel Channel,
    CombatHitOutcome Outcome,
    uint Damage,
    CombatRollEvidence Rolls,
    CombatDamageEvidence Evidence)
{
    public bool Hit => Outcome is not CombatHitOutcome.Miss;
    public bool IsCritical => Outcome is CombatHitOutcome.Critical;
    public uint CapturedDamageValue => Outcome == CombatHitOutcome.Miss
        ? uint.MaxValue
        : Damage;
}
