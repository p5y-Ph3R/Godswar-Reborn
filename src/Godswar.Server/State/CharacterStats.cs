namespace Godswar.Server.State;

internal sealed class CharacterStats
{
    public int CharacterId { get; init; }

    public int AccountId { get; init; }

    public string Name { get; init; } = string.Empty;

    public byte Profession { get; init; }

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

    public int StatusHit { get; init; }

    public int StatusResistance { get; init; }

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

    // Percentage combat channels use basis points (100 = 1%).
    public int PhysicalDamageReduction { get; init; }

    public int MagicDamageReduction { get; init; }

    public int CriticalDamageReduction { get; init; }

    public int LifeAbsorption { get; init; }

    public int LifeAbsorptionFlat { get; init; }

    public int DamageRebound { get; init; }

    // Flat combat channels use whole HP/damage units.
    public int PhysicalFlatAbsorption { get; init; }

    public int MagicFlatAbsorption { get; init; }

    public int CriticalDamageFlatReduction { get; init; }

    public int DamageReboundFlat { get; init; }

    public int BasicAttackIntervalMilliseconds { get; init; } = 1500;

    public float BasicAttackRange { get; init; } = 1.7f;

    public int WeaponScore { get; init; }

    public short WeaponRank { get; init; }

    public int WeaponAuraEffect { get; init; }

    public int ArmorScore { get; init; }

    public short ArmorRank { get; init; }

    public int ArmorAuraEffect { get; init; }

    public int LearnedSkillCount { get; init; }

    public void ApplyTo(GameCharacter character)
    {
        lock (character.VitalsSync)
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
            character.MarkVitalsChanged();
        }
    }

    public string ToLogSummary()
    {
        return $"hp={CurrentHp}/{MaxHp} mp={CurrentMp}/{MaxMp} " +
            $"patk={PhysicalAttack} pdef={PhysicalDefense} matk={MagicAttack} mdef={MagicDefense} " +
            $"hit={Hit} dodge={Dodge} status={StatusHit}/{StatusResistance} " +
            $"wr={WeaponRank}:{WeaponScore}/aura{WeaponAuraEffect} " +
            $"ar={ArmorRank}:{ArmorScore}/aura{ArmorAuraEffect} skills={LearnedSkillCount}";
    }

    internal CharacterStats WithCoreCombatAttributeBonus(
        int physicalAttackBasisPoints,
        int magicAttackBasisPoints,
        int physicalDefenseBasisPoints,
        int magicDefenseBasisPoints) =>
        new()
        {
            CharacterId = CharacterId,
            AccountId = AccountId,
            Name = Name,
            Profession = Profession,
            Level = Level,
            MaxHp = MaxHp,
            MaxMp = MaxMp,
            CurrentHp = CurrentHp,
            CurrentMp = CurrentMp,
            PhysicalAttack = ScaleByBonus(
                PhysicalAttack,
                physicalAttackBasisPoints),
            PhysicalDefense = ScaleByBonus(
                PhysicalDefense,
                physicalDefenseBasisPoints),
            MagicAttack = ScaleByBonus(
                MagicAttack,
                magicAttackBasisPoints),
            MagicDefense = ScaleByBonus(
                MagicDefense,
                magicDefenseBasisPoints),
            Hit = Hit,
            Dodge = Dodge,
            StatusHit = StatusHit,
            StatusResistance = StatusResistance,
            Critical = Critical,
            CriticalResistance = CriticalResistance,
            DamageAbsorb = DamageAbsorb,
            PhysicalDamageBonus = PhysicalDamageBonus,
            MagicDamageBonus = MagicDamageBonus,
            CureBonus = CureBonus,
            BeCureBonus = BeCureBonus,
            HpRecovery = HpRecovery,
            MpRecovery = MpRecovery,
            IgnorePhysicalDefense = IgnorePhysicalDefense,
            IgnoreMagicDefense = IgnoreMagicDefense,
            PhysicalAppendDamage = PhysicalAppendDamage,
            MagicAppendDamage = MagicAppendDamage,
            CriticalDamagePercent = CriticalDamagePercent,
            CriticalDamageFlat = CriticalDamageFlat,
            PhysicalDamageReduction = PhysicalDamageReduction,
            MagicDamageReduction = MagicDamageReduction,
            CriticalDamageReduction = CriticalDamageReduction,
            LifeAbsorption = LifeAbsorption,
            LifeAbsorptionFlat = LifeAbsorptionFlat,
            DamageRebound = DamageRebound,
            PhysicalFlatAbsorption = PhysicalFlatAbsorption,
            MagicFlatAbsorption = MagicFlatAbsorption,
            CriticalDamageFlatReduction = CriticalDamageFlatReduction,
            DamageReboundFlat = DamageReboundFlat,
            BasicAttackIntervalMilliseconds =
                BasicAttackIntervalMilliseconds,
            BasicAttackRange = BasicAttackRange,
            WeaponScore = WeaponScore,
            WeaponRank = WeaponRank,
            WeaponAuraEffect = WeaponAuraEffect,
            ArmorScore = ArmorScore,
            ArmorRank = ArmorRank,
            ArmorAuraEffect = ArmorAuraEffect,
            LearnedSkillCount = LearnedSkillCount
        };

    private static int ScaleByBonus(int value, int bonusBasisPoints)
    {
        if (value <= 0 || bonusBasisPoints <= 0)
        {
            return value;
        }

        var scaled = ((long)value * (10_000L + bonusBasisPoints)) + 5_000L;
        return (int)Math.Min(int.MaxValue, scaled / 10_000L);
    }

    public static CharacterStats FromCharacter(GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var baseline = character.CalculatedStats ?? new CharacterStats
        {
            CharacterId = character.Id,
            AccountId = character.AccountId,
            Name = character.Name,
            Profession = character.Profession,
            Level = character.Level,
            MaxHp = character.MaxHp,
            MaxMp = character.MaxMp,
            CurrentHp = character.CurrentHp,
            CurrentMp = character.CurrentMp,
            WeaponRank = character.WeaponRank,
            WeaponAuraEffect = character.WeaponAuraEffect,
            ArmorRank = character.ArmorRank,
            ArmorAuraEffect = character.ArmorAuraEffect,
            BasicAttackIntervalMilliseconds = 1500,
            BasicAttackRange = 1.7f
        };
        return MedusaTitleAttributePolicy.ApplyStrongestOwned(
            character.OwnedTitleIds,
            baseline);
    }
}
