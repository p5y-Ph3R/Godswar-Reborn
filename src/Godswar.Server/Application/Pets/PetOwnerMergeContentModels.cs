namespace Godswar.Server.Application.Pets;

internal sealed record PetOwnerMergeContentRevision(
    string Sha256,
    string PolicyVersion,
    int EffectBaseCount,
    int BandCount,
    int RateCount,
    string Source);

internal enum PetOwnerMergeSavvyStat : short
{
    Agility = 1,
    Strength = 2,
    Accuracy = 3,
    Technique = 4,
    Wisdom = 5,
    Luck = 6
}

internal enum PetOwnerMergeEffectCode : short
{
    MaxHealth = 0,
    MaxMana = 1,
    HitRate = 2,
    DodgeRate = 3,
    PhysicalAttack = 4,
    PhysicalDefense = 5,
    MagicAttack = 6,
    MagicDefense = 7,
    DamageAbsorption = 10,
    PhysicalDamageIncrease = 23,
    MagicDamageIncrease = 24,
    PhysicalDamageReduction = 29,
    MagicDamageReduction = 30,
    CriticalDamageReduction = 32,
    LifeAbsorption = 34,
    DamageRebound = 38
}

internal sealed record PetOwnerMergeEffectBaseContentDefinition(
    PetOwnerMergeEffectCode Effect,
    decimal BaseValue);

internal sealed record PetOwnerMergeBandContentDefinition(
    short BandIndex,
    decimal MinimumSavvy,
    decimal? MaximumSavvy);

internal sealed record PetOwnerMergeRateContentDefinition(
    PetOwnerMergeSavvyStat SourceSavvy,
    PetOwnerMergeEffectCode Effect,
    short BandIndex,
    decimal RatePerSavvy);
