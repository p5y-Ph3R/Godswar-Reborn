using System.Data.Common;

namespace Godswar.Server.State;

internal static class CharacterStatsReader
{
    public static CharacterStats Read(DbDataReader reader) =>
        new()
        {
            CharacterId = reader.GetInt32(0),
            AccountId = reader.GetInt32(1),
            Name = reader.GetString(2),
            Profession = checked((byte)reader.GetInt16(3)),
            Level = reader.GetInt32(4),
            MaxHp = reader.GetInt32(5),
            MaxMp = reader.GetInt32(6),
            CurrentHp = reader.GetInt32(7),
            CurrentMp = reader.GetInt32(8),
            PhysicalAttack = reader.GetInt32(9),
            PhysicalDefense = reader.GetInt32(10),
            MagicAttack = reader.GetInt32(11),
            MagicDefense = reader.GetInt32(12),
            Hit = reader.GetInt32(13),
            Dodge = reader.GetInt32(14),
            Critical = reader.GetInt32(15),
            CriticalResistance = reader.GetInt32(16),
            DamageAbsorb = reader.GetInt32(17),
            PhysicalDamageBonus = reader.GetInt32(18),
            MagicDamageBonus = reader.GetInt32(19),
            CureBonus = reader.GetInt32(20),
            BeCureBonus = reader.GetInt32(21),
            HpRecovery = reader.GetInt32(22),
            MpRecovery = reader.GetInt32(23),
            IgnorePhysicalDefense = reader.GetInt32(24),
            IgnoreMagicDefense = reader.GetInt32(25),
            PhysicalAppendDamage = reader.GetInt32(26),
            MagicAppendDamage = reader.GetInt32(27),
            CriticalDamagePercent = reader.GetInt32(28),
            CriticalDamageFlat = reader.GetInt32(29),
            WeaponScore = reader.GetInt32(30),
            WeaponRank = reader.GetInt16(31),
            WeaponAuraEffect = reader.GetInt32(32),
            ArmorScore = reader.GetInt32(33),
            ArmorRank = reader.GetInt16(34),
            ArmorAuraEffect = reader.GetInt32(35),
            LearnedSkillCount = reader.GetInt32(36),
            PhysicalDamageReduction = reader.GetInt32(37),
            MagicDamageReduction = reader.GetInt32(38),
            CriticalDamageReduction = reader.GetInt32(39),
            LifeAbsorption = reader.GetInt32(40),
            DamageRebound = reader.GetInt32(41),
            PhysicalFlatAbsorption = reader.GetInt32(42),
            MagicFlatAbsorption = reader.GetInt32(43),
            CriticalDamageFlatReduction = reader.GetInt32(44),
            DamageReboundFlat = reader.GetInt32(45),
            BasicAttackIntervalMilliseconds = reader.GetInt32(46),
            BasicAttackRange = reader.GetFloat(47)
        };
}
