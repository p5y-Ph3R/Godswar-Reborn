using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

/// <summary>
/// Reviewed bootstrap balance used when no official owner-Merge publication
/// exists or when upgrading its exact reviewed predecessor. Once published,
/// PostgreSQL is authoritative and the process pins that immutable revision
/// until restart.
/// </summary>
internal static class PetOwnerMergeContentBaseline
{
    public const string Source = "reviewed-pet-owner-merge-v3";

    public const string PolicyVersion =
        "project-pet-unite-piecewise-marginal-v4";

    private static readonly decimal[] BandMultipliers =
        [1.00m, 0.85m, 0.70m, 0.60m, 0.50m];

    public static PinnedPetOwnerMergeContentCatalog Create()
    {
        var bases = CreateEffectBases();
        var bands = CreateBands();
        var rates = CreateRateSeeds()
            .SelectMany(seed => BandMultipliers.Select(
                (multiplier, index) =>
                    new PetOwnerMergeRateContentDefinition(
                        seed.Source,
                        seed.Effect,
                        checked((short)(index + 1)),
                        seed.FirstBandRate * multiplier)))
            .ToArray();
        return PinnedPetOwnerMergeContentCatalog.Create(
            Source,
            PolicyVersion,
            bases,
            bands,
            rates);
    }

    private static PetOwnerMergeEffectBaseContentDefinition[]
        CreateEffectBases() =>
    [
        B(PetOwnerMergeEffectCode.MaxHealth, 4000m),
        B(PetOwnerMergeEffectCode.MaxMana, 300m),
        B(PetOwnerMergeEffectCode.HitRate, 20m),
        B(PetOwnerMergeEffectCode.DodgeRate, 10m),
        B(PetOwnerMergeEffectCode.PhysicalAttack, 100m),
        B(PetOwnerMergeEffectCode.PhysicalDefense, 80m),
        B(PetOwnerMergeEffectCode.MagicAttack, 80m),
        B(PetOwnerMergeEffectCode.MagicDefense, 60m),
        B(PetOwnerMergeEffectCode.DamageAbsorption, 80m),
        B(PetOwnerMergeEffectCode.PhysicalDamageIncrease, 200m),
        B(PetOwnerMergeEffectCode.MagicDamageIncrease, 150m),
        B(PetOwnerMergeEffectCode.PhysicalDamageReduction, 600m),
        B(PetOwnerMergeEffectCode.MagicDamageReduction, 480m),
        B(PetOwnerMergeEffectCode.CriticalDamageReduction, 800m),
        B(PetOwnerMergeEffectCode.LifeAbsorption, 100m),
        B(PetOwnerMergeEffectCode.DamageRebound, 150m)
    ];

    private static PetOwnerMergeBandContentDefinition[] CreateBands() =>
    [
        new(1, 0m, 60m),
        new(2, 60m, 150m),
        new(3, 150m, 300m),
        new(4, 300m, 600m),
        new(5, 600m, null)
    ];

    private static OwnerMergeRateSeed[] CreateRateSeeds() =>
    [
        R(PetOwnerMergeSavvyStat.Agility,
            PetOwnerMergeEffectCode.MaxMana, 4m),
        R(PetOwnerMergeSavvyStat.Agility,
            PetOwnerMergeEffectCode.MagicAttack, 2m),
        R(PetOwnerMergeSavvyStat.Agility,
            PetOwnerMergeEffectCode.DamageRebound, 0m),
        R(PetOwnerMergeSavvyStat.Agility,
            PetOwnerMergeEffectCode.HitRate, 0.12m),
        R(PetOwnerMergeSavvyStat.Strength,
            PetOwnerMergeEffectCode.MaxHealth, 10m),
        R(PetOwnerMergeSavvyStat.Strength,
            PetOwnerMergeEffectCode.PhysicalDefense, 2m),
        R(PetOwnerMergeSavvyStat.Strength,
            PetOwnerMergeEffectCode.LifeAbsorption, 5m),
        R(PetOwnerMergeSavvyStat.Accuracy,
            PetOwnerMergeEffectCode.HitRate, 0.48m),
        R(PetOwnerMergeSavvyStat.Accuracy,
            PetOwnerMergeEffectCode.PhysicalAttack, 3m),
        R(PetOwnerMergeSavvyStat.Accuracy,
            PetOwnerMergeEffectCode.MagicDefense, 1.5m),
        R(PetOwnerMergeSavvyStat.Technique,
            PetOwnerMergeEffectCode.DodgeRate, 0.5m),
        R(PetOwnerMergeSavvyStat.Technique,
            PetOwnerMergeEffectCode.PhysicalDamageReduction, 12m),
        R(PetOwnerMergeSavvyStat.Technique,
            PetOwnerMergeEffectCode.MagicDamageReduction, 10m),
        R(PetOwnerMergeSavvyStat.Wisdom,
            PetOwnerMergeEffectCode.MaxHealth, 40m),
        R(PetOwnerMergeSavvyStat.Wisdom,
            PetOwnerMergeEffectCode.PhysicalDamageIncrease, 5m),
        R(PetOwnerMergeSavvyStat.Wisdom,
            PetOwnerMergeEffectCode.CriticalDamageReduction, 15m),
        R(PetOwnerMergeSavvyStat.Luck,
            PetOwnerMergeEffectCode.DamageAbsorption, 1.5m),
        R(PetOwnerMergeSavvyStat.Luck,
            PetOwnerMergeEffectCode.MagicDamageIncrease, 4m),
        R(PetOwnerMergeSavvyStat.Luck,
            PetOwnerMergeEffectCode.DamageRebound, 6m)
    ];

    private static PetOwnerMergeEffectBaseContentDefinition B(
        PetOwnerMergeEffectCode effect,
        decimal value) => new(effect, value);

    private static OwnerMergeRateSeed R(
        PetOwnerMergeSavvyStat source,
        PetOwnerMergeEffectCode effect,
        decimal firstBandRate) => new(source, effect, firstBandRate);

    private sealed record OwnerMergeRateSeed(
        PetOwnerMergeSavvyStat Source,
        PetOwnerMergeEffectCode Effect,
        decimal FirstBandRate);
}
