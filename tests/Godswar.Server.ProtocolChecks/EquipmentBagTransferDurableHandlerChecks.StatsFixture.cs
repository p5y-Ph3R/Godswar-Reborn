using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private static CharacterStats CopyStats(
        CharacterStats source,
        int maxHp,
        int maxMp,
        int physicalAttack,
        int? physicalDamageReduction = null,
        int? magicDamageReduction = null,
        int? criticalDamageReduction = null,
        int? lifeAbsorption = null,
        int? damageRebound = null,
        int? physicalFlatAbsorption = null,
        int? magicFlatAbsorption = null,
        int? criticalDamageFlatReduction = null,
        int? damageReboundFlat = null,
        int? basicAttackIntervalMilliseconds = null,
        float? basicAttackRange = null) =>
        new()
        {
            CharacterId = source.CharacterId,
            AccountId = source.AccountId,
            Name = source.Name,
            Profession = source.Profession,
            Level = source.Level,
            MaxHp = maxHp,
            MaxMp = maxMp,
            CurrentHp = source.CurrentHp,
            CurrentMp = source.CurrentMp,
            PhysicalAttack = physicalAttack,
            PhysicalDefense = source.PhysicalDefense,
            MagicAttack = source.MagicAttack,
            MagicDefense = source.MagicDefense,
            Hit = source.Hit,
            Dodge = source.Dodge,
            Critical = source.Critical,
            CriticalResistance = source.CriticalResistance,
            DamageAbsorb = source.DamageAbsorb,
            PhysicalDamageBonus = source.PhysicalDamageBonus,
            MagicDamageBonus = source.MagicDamageBonus,
            CureBonus = source.CureBonus,
            BeCureBonus = source.BeCureBonus,
            HpRecovery = source.HpRecovery,
            MpRecovery = source.MpRecovery,
            IgnorePhysicalDefense = source.IgnorePhysicalDefense,
            IgnoreMagicDefense = source.IgnoreMagicDefense,
            PhysicalAppendDamage = source.PhysicalAppendDamage,
            MagicAppendDamage = source.MagicAppendDamage,
            CriticalDamagePercent = source.CriticalDamagePercent,
            CriticalDamageFlat = source.CriticalDamageFlat,
            PhysicalDamageReduction = physicalDamageReduction ??
                source.PhysicalDamageReduction,
            MagicDamageReduction = magicDamageReduction ??
                source.MagicDamageReduction,
            CriticalDamageReduction = criticalDamageReduction ??
                source.CriticalDamageReduction,
            LifeAbsorption = lifeAbsorption ?? source.LifeAbsorption,
            DamageRebound = damageRebound ?? source.DamageRebound,
            PhysicalFlatAbsorption = physicalFlatAbsorption ??
                source.PhysicalFlatAbsorption,
            MagicFlatAbsorption = magicFlatAbsorption ??
                source.MagicFlatAbsorption,
            CriticalDamageFlatReduction = criticalDamageFlatReduction ??
                source.CriticalDamageFlatReduction,
            DamageReboundFlat = damageReboundFlat ??
                source.DamageReboundFlat,
            BasicAttackIntervalMilliseconds =
                basicAttackIntervalMilliseconds ??
                source.BasicAttackIntervalMilliseconds,
            BasicAttackRange = basicAttackRange ?? source.BasicAttackRange,
            WeaponScore = source.WeaponScore,
            WeaponRank = source.WeaponRank,
            WeaponAuraEffect = source.WeaponAuraEffect,
            ArmorScore = source.ArmorScore,
            ArmorRank = source.ArmorRank,
            ArmorAuraEffect = source.ArmorAuraEffect,
            LearnedSkillCount = source.LearnedSkillCount
        };
}
