namespace Godswar.Server.State;

internal sealed class CharacterStats
{
    public int CharacterId { get; init; }

    public int AccountId { get; init; }

    public string Name { get; init; } = string.Empty;

    public int Level { get; init; }

    public int MaxHp { get; init; }

    public int MaxMp { get; init; }

    public int CurrentHp { get; init; }

    public int CurrentMp { get; init; }

    public int PhysicalAttack { get; init; }

    public int PhysicalDefense { get; init; }

    public int MagicAttack { get; init; }

    public int MagicDefense { get; init; }

    public int Hit { get; init; }

    public int Dodge { get; init; }

    public int Critical { get; init; }

    public int CriticalResistance { get; init; }

    public int DamageAbsorb { get; init; }

    public int PhysicalDamageBonus { get; init; }

    public int MagicDamageBonus { get; init; }

    public int CureBonus { get; init; }

    public int BeCureBonus { get; init; }

    public int HpRecovery { get; init; }

    public int MpRecovery { get; init; }

    public int IgnorePhysicalDefense { get; init; }

    public int IgnoreMagicDefense { get; init; }

    public int PhysicalAppendDamage { get; init; }

    public int MagicAppendDamage { get; init; }

    public int CriticalDamagePercent { get; init; }

    public int CriticalDamageFlat { get; init; }

    public int WeaponScore { get; init; }

    public short WeaponRank { get; init; }

    public int WeaponAuraEffect { get; init; }

    public int ArmorScore { get; init; }

    public short ArmorRank { get; init; }

    public int ArmorAuraEffect { get; init; }

    public int LearnedSkillCount { get; init; }

    public void ApplyTo(GameCharacter character)
    {
        character.MaxHp = Math.Max(1, MaxHp);
        character.MaxMp = Math.Max(0, MaxMp);
        character.CurrentHp = Math.Clamp(CurrentHp, 0, character.MaxHp);
        character.CurrentMp = Math.Clamp(CurrentMp, 0, Math.Max(1, character.MaxMp));
        character.WeaponRank = WeaponRank;
        character.WeaponAuraEffect = WeaponAuraEffect;
        character.ArmorRank = ArmorRank;
        character.ArmorAuraEffect = ArmorAuraEffect;
        character.CalculatedStats = this;
    }

    public string ToLogSummary()
    {
        return $"hp={CurrentHp}/{MaxHp} mp={CurrentMp}/{MaxMp} " +
            $"patk={PhysicalAttack} pdef={PhysicalDefense} matk={MagicAttack} mdef={MagicDefense} " +
            $"hit={Hit} dodge={Dodge} wr={WeaponRank}:{WeaponScore}/aura{WeaponAuraEffect} " +
            $"ar={ArmorRank}:{ArmorScore}/aura{ArmorAuraEffect} skills={LearnedSkillCount}";
    }

    public static CharacterStats FromCharacter(GameCharacter character)
    {
        return new CharacterStats
        {
            CharacterId = character.Id,
            AccountId = character.AccountId,
            Name = character.Name,
            Level = character.Level,
            MaxHp = character.MaxHp,
            MaxMp = character.MaxMp,
            CurrentHp = character.CurrentHp,
            CurrentMp = character.CurrentMp,
            WeaponRank = character.WeaponRank,
            WeaponAuraEffect = character.WeaponAuraEffect,
            ArmorRank = character.ArmorRank,
            ArmorAuraEffect = character.ArmorAuraEffect
        };
    }
}
