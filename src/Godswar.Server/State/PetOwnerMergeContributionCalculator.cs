using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal readonly record struct PetOwnerMergeEffectValue(
    PetOwnerMergeEffectCode Effect,
    decimal Value);

/// <summary>
/// Project-authored interpretation of the stock Pet_Alter.xml Unite table.
/// Restrict boundaries are treated as continuous marginal bands: each rate
/// applies only to savvy inside its band. This keeps every channel monotonic
/// at 60/150/300/600, unlike multiplying the entire value by the current
/// band's declining rate. Overlapping traits are summed, Base is added once,
/// and only the final channel is rounded to the database's six-decimal scale
/// using midpoint-away-from-zero. Native interpolation remains unrecovered.
/// </summary>
internal static class PetOwnerMergeContributionCalculator
{
    public const int DecimalScale = 6;
    public const decimal TechniqueReductionBasisPointsPerSavvy = 0.15m;
    public const decimal MaximumTechniqueReductionBasisPoints = 3_000m;

    public static PetOwnerStatContribution Calculate(
        PetSavvy totalSavvy,
        IPetOwnerMergeContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!totalSavvy.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSavvy));
        }

        var values = content.EffectBases.ToDictionary(
            static value => value.Effect,
            static value => value.BaseValue);
        var bands = content.Bands.ToDictionary(
            static value => value.BandIndex);
        foreach (var rate in content.Rates)
        {
            if (!values.ContainsKey(rate.Effect) ||
                !bands.TryGetValue(rate.BandIndex, out var band))
            {
                throw new InvalidDataException(
                    "The pinned owner-Merge balance is internally inconsistent.");
            }

            var savvy = ResolveSavvy(totalSavvy, rate.SourceSavvy);
            var width = WidthInsideBand(savvy, band);
            values[rate.Effect] = checked(
                values[rate.Effect] + (width * rate.RatePerSavvy));
        }

        var native = FromEffectValues(values.Select(static pair =>
            new PetOwnerMergeEffectValue(pair.Key, Round(pair.Value))));
        var techniqueReduction = CalculateTechniqueReductionBasisPoints(
            totalSavvy.Technique);
        return native with
        {
            TechniquePhysicalReduction = techniqueReduction,
            TechniqueMagicReduction = techniqueReduction
        };
    }

    public static decimal CalculateTechniqueReductionBasisPoints(
        decimal effectiveTechnique)
    {
        if (effectiveTechnique < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveTechnique));
        }

        return Math.Min(
            MaximumTechniqueReductionBasisPoints,
            Math.Round(
                effectiveTechnique *
                    TechniqueReductionBasisPointsPerSavvy,
                0,
                MidpointRounding.AwayFromZero));
    }

    public static IReadOnlyList<PetOwnerMergeEffectValue> ToEffectValues(
        PetOwnerStatContribution contribution)
    {
        if (!contribution.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(contribution));
        }

        return
        [
            E(PetOwnerMergeEffectCode.MaxHealth, contribution.MaxHealth),
            E(PetOwnerMergeEffectCode.MaxMana, contribution.MaxMana),
            E(PetOwnerMergeEffectCode.HitRate, contribution.HitRate),
            E(PetOwnerMergeEffectCode.DodgeRate, contribution.DodgeRate),
            E(PetOwnerMergeEffectCode.PhysicalAttack, contribution.PhysicalAttack),
            E(PetOwnerMergeEffectCode.PhysicalDefense, contribution.PhysicalDefense),
            E(PetOwnerMergeEffectCode.MagicAttack, contribution.MagicAttack),
            E(PetOwnerMergeEffectCode.MagicDefense, contribution.MagicDefense),
            E(PetOwnerMergeEffectCode.DamageAbsorption, contribution.DamageAbsorption),
            E(PetOwnerMergeEffectCode.PhysicalDamageIncrease, contribution.PhysicalDamageIncrease),
            E(PetOwnerMergeEffectCode.MagicDamageIncrease, contribution.MagicDamageIncrease),
            E(PetOwnerMergeEffectCode.PhysicalDamageReduction, contribution.PhysicalDamageReduction),
            E(PetOwnerMergeEffectCode.MagicDamageReduction, contribution.MagicDamageReduction),
            E(PetOwnerMergeEffectCode.CriticalDamageReduction, contribution.CriticalDamageReduction),
            E(PetOwnerMergeEffectCode.LifeAbsorption, contribution.LifeAbsorption),
            E(PetOwnerMergeEffectCode.DamageRebound, contribution.DamageRebound)
        ];
    }

    public static PetOwnerStatContribution FromEffectValues(
        IEnumerable<PetOwnerMergeEffectValue> effectValues)
    {
        ArgumentNullException.ThrowIfNull(effectValues);
        var values = new Dictionary<PetOwnerMergeEffectCode, decimal>();
        foreach (var value in effectValues)
        {
            if (!Enum.IsDefined(value.Effect) ||
                value.Value < 0m ||
                !values.TryAdd(value.Effect, value.Value))
            {
                throw new InvalidDataException(
                    "Pet owner-merge effects are invalid or duplicated.");
            }
        }

        decimal Get(PetOwnerMergeEffectCode code) =>
            values.GetValueOrDefault(code);

        return new PetOwnerStatContribution(
            Get(PetOwnerMergeEffectCode.MaxHealth),
            Get(PetOwnerMergeEffectCode.HitRate),
            Get(PetOwnerMergeEffectCode.PhysicalAttack),
            Get(PetOwnerMergeEffectCode.PhysicalDamageIncrease),
            Get(PetOwnerMergeEffectCode.PhysicalDefense),
            Get(PetOwnerMergeEffectCode.PhysicalDamageReduction),
            Get(PetOwnerMergeEffectCode.DamageAbsorption),
            Get(PetOwnerMergeEffectCode.LifeAbsorption),
            Get(PetOwnerMergeEffectCode.MaxMana),
            Get(PetOwnerMergeEffectCode.DodgeRate),
            Get(PetOwnerMergeEffectCode.MagicAttack),
            Get(PetOwnerMergeEffectCode.MagicDamageIncrease),
            Get(PetOwnerMergeEffectCode.MagicDefense),
            Get(PetOwnerMergeEffectCode.MagicDamageReduction),
            Get(PetOwnerMergeEffectCode.CriticalDamageReduction),
            Get(PetOwnerMergeEffectCode.DamageRebound));
    }

    private static decimal ResolveSavvy(
        PetSavvy savvy,
        PetOwnerMergeSavvyStat source) =>
        source switch
        {
            PetOwnerMergeSavvyStat.Agility => savvy.Agility,
            PetOwnerMergeSavvyStat.Strength => savvy.Strength,
            PetOwnerMergeSavvyStat.Accuracy => savvy.Accuracy,
            PetOwnerMergeSavvyStat.Technique => savvy.Technique,
            PetOwnerMergeSavvyStat.Wisdom => savvy.Wisdom,
            PetOwnerMergeSavvyStat.Luck => savvy.Luck,
            _ => throw new InvalidDataException(
                "The pinned owner-Merge balance has an unknown Savvy source.")
        };

    private static decimal WidthInsideBand(
        decimal savvy,
        PetOwnerMergeBandContentDefinition band)
    {
        if (savvy <= band.MinimumSavvy)
        {
            return 0m;
        }

        var upper = band.MaximumSavvy ?? savvy;
        return Math.Min(savvy, upper) - band.MinimumSavvy;
    }

    private static PetOwnerMergeEffectValue E(
        PetOwnerMergeEffectCode effect,
        decimal value) => new(effect, Round(value));

    private static decimal Round(decimal value) =>
        Math.Round(value, DecimalScale, MidpointRounding.AwayFromZero);
}
