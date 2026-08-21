using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

internal static class CombatCharacterStatsAdapter
{
    public static CombatAttackerStats FromCharacter(GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var stats = character.CalculatedStats ??
                    CharacterStats.FromCharacter(character);
        return FromStats(character.Profession, character.Level, stats);
    }

    public static CombatAttackerStats FromStats(
        byte profession,
        int level,
        CharacterStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new CombatAttackerStats
        {
            Level = level,
            Profession = profession,
            PhysicalAttack = stats.PhysicalAttack,
            MagicAttack = stats.MagicAttack,
            Hit = stats.Hit,
            Critical = stats.Critical,
            PhysicalDamageBonusBasisPoints = stats.PhysicalDamageBonus,
            MagicDamageBonusBasisPoints = stats.MagicDamageBonus,
            PhysicalAppendDamage = stats.PhysicalAppendDamage,
            MagicAppendDamage = stats.MagicAppendDamage,
            IgnorePhysicalDefenseBasisPoints =
                stats.IgnorePhysicalDefense,
            IgnoreMagicDefenseBasisPoints = stats.IgnoreMagicDefense,
            CriticalDamageBasisPoints = stats.CriticalDamagePercent,
            CriticalDamageFlat = stats.CriticalDamageFlat,
            LifeAbsorptionBasisPoints = stats.LifeAbsorption,
            LifeAbsorptionFlat = stats.LifeAbsorptionFlat
        };
    }

    public static CombatAttackerStats FromOffense(
        in PlayerCombatOffenseComponent offense) =>
        new()
        {
            Level = offense.Level,
            Profession = offense.Profession,
            PhysicalAttack = offense.PhysicalAttack,
            MagicAttack = offense.MagicAttack,
            Hit = offense.Hit,
            Critical = offense.Critical,
            PhysicalDamageBonusBasisPoints = offense.PhysicalDamageBonus,
            MagicDamageBonusBasisPoints = offense.MagicDamageBonus,
            PhysicalAppendDamage = offense.PhysicalAppendDamage,
            MagicAppendDamage = offense.MagicAppendDamage,
            IgnorePhysicalDefenseBasisPoints =
                offense.IgnorePhysicalDefenseBasisPoints,
            IgnoreMagicDefenseBasisPoints =
                offense.IgnoreMagicDefenseBasisPoints,
            CriticalDamageBasisPoints =
                offense.CriticalDamageBasisPoints,
            CriticalDamageFlat = offense.CriticalDamageFlat,
            LifeAbsorptionBasisPoints = offense.LifeAbsorptionBasisPoints,
            LifeAbsorptionFlat = offense.LifeAbsorptionFlat
        };

    public static CombatAttackerStats ApplyRuntimeAttackerModifiers(
        in CombatAttackerStats attacker,
        in ClientStatusAggregate modifiers) =>
        attacker with
        {
            Hit = SaturatingAdd(attacker.Hit, modifiers.Hit),
            Critical = SaturatingAdd(
                attacker.Critical,
                modifiers.CriticalAppend)
        };

    public static CombatTargetStats ApplyRuntimeTargetModifiers(
        in CombatTargetStats target,
        in ClientStatusAggregate modifiers) =>
        target with
        {
            Dodge = SaturatingAdd(target.Dodge, modifiers.Dodge),
            CriticalResistance = SaturatingAdd(
                target.CriticalResistance,
                modifiers.CriticalResistance)
        };

    public static CombatTargetStats ToTarget(
        int level,
        CharacterStats stats,
        int physicalDefenseBonus = 0,
        int magicDefenseBonus = 0,
        int physicalReductionBonusBasisPoints = 0,
        int magicReductionBonusBasisPoints = 0,
        int physicalDamageTakenIncreaseBasisPoints = 0,
        int magicDamageTakenIncreaseBasisPoints = 0)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new CombatTargetStats
        {
            Level = level,
            PhysicalDefense = SaturatingAdd(
                stats.PhysicalDefense,
                physicalDefenseBonus),
            MagicDefense = SaturatingAdd(
                stats.MagicDefense,
                magicDefenseBonus),
            Dodge = stats.Dodge,
            CriticalResistance = stats.CriticalResistance,
            PhysicalDamageReductionBasisPoints = SaturatingAdd(
                stats.PhysicalDamageReduction,
                physicalReductionBonusBasisPoints),
            MagicDamageReductionBasisPoints = SaturatingAdd(
                stats.MagicDamageReduction,
                magicReductionBonusBasisPoints),
            PhysicalDamageTakenIncreaseBasisPoints = Math.Max(
                0,
                physicalDamageTakenIncreaseBasisPoints),
            MagicDamageTakenIncreaseBasisPoints = Math.Max(
                0,
                magicDamageTakenIncreaseBasisPoints),
            CriticalDamageReductionBasisPoints =
                stats.CriticalDamageReduction,
            PhysicalFlatAbsorption =
                Math.Max(0, stats.PhysicalFlatAbsorption),
            MagicFlatAbsorption =
                Math.Max(0, stats.MagicFlatAbsorption),
            CriticalDamageFlatReduction =
                Math.Max(0, stats.CriticalDamageFlatReduction),
            DamageReboundBasisPoints = stats.DamageRebound,
            DamageReboundFlat = Math.Max(0, stats.DamageReboundFlat)
        };
    }

    private static int SaturatingAdd(int left, int right) =>
        (int)Math.Clamp((long)left + right, int.MinValue, int.MaxValue);
}
