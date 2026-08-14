namespace Godswar.Server.State;

/// <summary>
/// The six savvy values used by the stock Pet_Alter.xml operations, in the
/// exact order presented by the pet UI. Both initial and added values use this
/// shape. Rarity-derived added savvy is retained as an immutable floor when a
/// rebirth clears later trainable additions.
/// </summary>
internal readonly record struct PetSavvy(
    decimal Agility,
    decimal Strength,
    decimal Accuracy,
    decimal Technique,
    decimal Wisdom,
    decimal Luck)
{
    public static PetSavvy Zero => default;

    public bool IsNonNegative =>
        Agility >= 0m &&
        Strength >= 0m &&
        Accuracy >= 0m &&
        Technique >= 0m &&
        Wisdom >= 0m &&
        Luck >= 0m;

    public decimal Total => checked(
        Agility + Strength + Accuracy + Technique + Wisdom + Luck);

    public bool IsAtLeast(PetSavvy other) =>
        Agility >= other.Agility &&
        Strength >= other.Strength &&
        Accuracy >= other.Accuracy &&
        Technique >= other.Technique &&
        Wisdom >= other.Wisdom &&
        Luck >= other.Luck;

    public bool HasAnyIncreaseOver(PetSavvy other) =>
        Agility > other.Agility ||
        Strength > other.Strength ||
        Accuracy > other.Accuracy ||
        Technique > other.Technique ||
        Wisdom > other.Wisdom ||
        Luck > other.Luck;

    public static PetSavvy operator +(PetSavvy left, PetSavvy right) =>
        new(
            left.Agility + right.Agility,
            left.Strength + right.Strength,
            left.Accuracy + right.Accuracy,
            left.Technique + right.Technique,
            left.Wisdom + right.Wisdom,
            left.Luck + right.Luck);
}

/// <summary>
/// The sixteen character contributions shown in PetUnite.xml. These values
/// must be calculated by an authoritative server policy, never accepted from
/// a client packet.
/// </summary>
internal readonly record struct PetOwnerStatContribution(
    decimal MaxHealth,
    decimal HitRate,
    decimal PhysicalAttack,
    decimal PhysicalDamageIncrease,
    decimal PhysicalDefense,
    decimal PhysicalDamageReduction,
    decimal DamageAbsorption,
    decimal LifeAbsorption,
    decimal MaxMana,
    decimal DodgeRate,
    decimal MagicAttack,
    decimal MagicDamageIncrease,
    decimal MagicDefense,
    decimal MagicDamageReduction,
    decimal CriticalDamageReduction,
    decimal DamageRebound)
{
    public static PetOwnerStatContribution Zero => default;

    public bool IsNonNegative =>
        MaxHealth >= 0m &&
        HitRate >= 0m &&
        PhysicalAttack >= 0m &&
        PhysicalDamageIncrease >= 0m &&
        PhysicalDefense >= 0m &&
        PhysicalDamageReduction >= 0m &&
        DamageAbsorption >= 0m &&
        LifeAbsorption >= 0m &&
        MaxMana >= 0m &&
        DodgeRate >= 0m &&
        MagicAttack >= 0m &&
        MagicDamageIncrease >= 0m &&
        MagicDefense >= 0m &&
        MagicDamageReduction >= 0m &&
        CriticalDamageReduction >= 0m &&
        DamageRebound >= 0m;
}

internal sealed record PetOwnerMergeState(
    PetOwnerStatContribution StatContribution,
    IReadOnlyList<int> GrantedSkillIds);

/// <summary>
/// Transport-independent authoritative pet state. Wire IDs and persistence
/// details intentionally stay outside this model until captured packet
/// evidence defines them.
/// </summary>
internal sealed record OwnedPet(
    long PetId,
    long OwnerCharacterId,
    int SpeciesType,
    string Name,
    int Level,
    long Experience,
    decimal Rank,
    PetAptitude Aptitude,
    PetSavvy InitialSavvy,
    PetSavvy AddedSavvy,
    PetSavvy BaseGrowthRates,
    PetSavvy GrowthAcceleration,
    int CompletedPetMerges,
    int CompletedRebirths,
    int RebirthsRemaining,
    bool HasSoulContract,
    bool HasOwnerMergeTalent,
    bool IsBound,
    bool IsSummoned,
    bool IsAway,
    int CurrentEnergy,
    int MaximumEnergy,
    int Amity,
    PetOwnerMergeState? OwnerMerge,
    PetSavvy RarityAddedSavvy = default,
    byte SoulContractStage = 0)
{
    public PetSavvy CurrentAddedSavvy =>
        PetSavvyRuntimeSemantics.ResolveMaterializedAdded(
            Level,
            AddedSavvy,
            BaseGrowthRates,
            GrowthAcceleration,
            RarityAddedSavvy == PetSavvy.Zero
                ? null
                : RarityAddedSavvy);

    public PetSavvy TotalSavvy =>
        PetSavvyRuntimeSemantics.ResolvePlayerVisibleTotal(
            Level,
            InitialSavvy,
            AddedSavvy,
            BaseGrowthRates,
            GrowthAcceleration,
            RarityAddedSavvy);

    public byte ProjectedSoulContractStage =>
        SoulContractStage == 0 && HasSoulContract
            ? (byte)1
            : SoulContractStage;

    /// <summary>
    /// Stock effective Savvy used by Unite and trait gates. Soul Contract is
    /// deliberately separate from raw Basic/Added so pet-to-pet Merge can
    /// continue to ignore it, as the installed-client guide requires.
    /// </summary>
    public PetSavvy EffectiveTotalSavvy =>
        PetSoulContractPolicy.ResolveDisplayedTotal(
            TotalSavvy,
            ProjectedSoulContractStage);

    public bool IsMergedWithOwner => OwnerMerge is not null;
}
