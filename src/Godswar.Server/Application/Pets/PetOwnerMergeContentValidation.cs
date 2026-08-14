namespace Godswar.Server.Application.Pets;

internal static class PetOwnerMergeContentValidation
{
    private const int RequiredBandCount = 5;

    private static readonly IReadOnlyDictionary<
        PetOwnerMergeSavvyStat,
        PetOwnerMergeEffectCode[]> ExpectedMappings =
        new Dictionary<PetOwnerMergeSavvyStat, PetOwnerMergeEffectCode[]>
        {
            [PetOwnerMergeSavvyStat.Agility] =
            [
                PetOwnerMergeEffectCode.MaxMana,
                PetOwnerMergeEffectCode.MagicAttack,
                PetOwnerMergeEffectCode.DamageRebound,
                PetOwnerMergeEffectCode.HitRate
            ],
            [PetOwnerMergeSavvyStat.Strength] =
            [
                PetOwnerMergeEffectCode.MaxHealth,
                PetOwnerMergeEffectCode.PhysicalDefense,
                PetOwnerMergeEffectCode.LifeAbsorption
            ],
            [PetOwnerMergeSavvyStat.Accuracy] =
            [
                PetOwnerMergeEffectCode.HitRate,
                PetOwnerMergeEffectCode.PhysicalAttack,
                PetOwnerMergeEffectCode.MagicDefense
            ],
            [PetOwnerMergeSavvyStat.Technique] =
            [
                PetOwnerMergeEffectCode.DodgeRate,
                PetOwnerMergeEffectCode.PhysicalDamageReduction,
                PetOwnerMergeEffectCode.MagicDamageReduction
            ],
            [PetOwnerMergeSavvyStat.Wisdom] =
            [
                PetOwnerMergeEffectCode.MaxHealth,
                PetOwnerMergeEffectCode.PhysicalDamageIncrease,
                PetOwnerMergeEffectCode.CriticalDamageReduction
            ],
            [PetOwnerMergeSavvyStat.Luck] =
            [
                PetOwnerMergeEffectCode.DamageAbsorption,
                PetOwnerMergeEffectCode.MagicDamageIncrease,
                PetOwnerMergeEffectCode.DamageRebound
            ]
        };

    public static void Validate(
        string source,
        string policyVersion,
        PetOwnerMergeEffectBaseContentDefinition[] effectBases,
        PetOwnerMergeBandContentDefinition[] bands,
        PetOwnerMergeRateContentDefinition[] rates)
    {
        if (source.Length > 96 || policyVersion.Length > 64)
        {
            throw new InvalidOperationException(
                "The pet owner-Merge source or policy version is oversized.");
        }

        ValidateEffectBases(effectBases);
        ValidateBands(bands);
        ValidateRates(bands, rates);
    }

    private static void ValidateEffectBases(
        PetOwnerMergeEffectBaseContentDefinition[] values)
    {
        var expected = Enum.GetValues<PetOwnerMergeEffectCode>();
        if (values.Length != expected.Length ||
            values.Select(static value => value.Effect).Distinct().Count() !=
                values.Length ||
            !values.Select(static value => value.Effect)
                .Order()
                .SequenceEqual(expected.Order()) ||
            values.Any(static value => value.BaseValue < 0m))
        {
            throw new InvalidOperationException(
                "Pet owner-Merge effect bases are incomplete, duplicated, or invalid.");
        }
    }

    private static void ValidateBands(
        PetOwnerMergeBandContentDefinition[] values)
    {
        if (values.Length != RequiredBandCount ||
            !values.Select(static value => (int)value.BandIndex)
                .SequenceEqual(Enumerable.Range(1, RequiredBandCount)) ||
            values[0].MinimumSavvy != 0m)
        {
            throw new InvalidOperationException(
                "Pet owner-Merge bands are incomplete or unordered.");
        }

        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var isLast = index == values.Length - 1;
            if (value.MinimumSavvy < 0m ||
                isLast != !value.MaximumSavvy.HasValue ||
                (!isLast && value.MaximumSavvy <= value.MinimumSavvy) ||
                (!isLast && value.MaximumSavvy !=
                    values[index + 1].MinimumSavvy))
            {
                throw new InvalidOperationException(
                    $"Pet owner-Merge band {value.BandIndex} is invalid or discontinuous.");
            }
        }
    }

    private static void ValidateRates(
        PetOwnerMergeBandContentDefinition[] bands,
        PetOwnerMergeRateContentDefinition[] values)
    {
        var expectedPairCount = ExpectedMappings.Sum(
            static value => value.Value.Length);
        if (values.Length != expectedPairCount * bands.Length ||
            values.Select(static value =>
                    (value.SourceSavvy, value.Effect, value.BandIndex))
                .Distinct().Count() != values.Length ||
            values.Any(static value => value.RatePerSavvy < 0m))
        {
            throw new InvalidOperationException(
                "Pet owner-Merge rates are incomplete, duplicated, or negative.");
        }

        foreach (var (source, effects) in ExpectedMappings)
        {
            foreach (var effect in effects)
            {
                var curve = values
                    .Where(value => value.SourceSavvy == source &&
                                    value.Effect == effect)
                    .OrderBy(static value => value.BandIndex)
                    .ToArray();
                if (curve.Length != bands.Length ||
                    !curve.Select(static value => (int)value.BandIndex)
                        .SequenceEqual(Enumerable.Range(1, bands.Length)))
                {
                    throw new InvalidOperationException(
                        $"Pet owner-Merge curve {source}/{effect} is incomplete.");
                }

                for (var index = 1; index < curve.Length; index++)
                {
                    if (curve[index].RatePerSavvy >
                        curve[index - 1].RatePerSavvy)
                    {
                        throw new InvalidOperationException(
                            $"Pet owner-Merge curve {source}/{effect} must not increase across later bands.");
                    }
                }
            }
        }

        var expectedPairs = ExpectedMappings
            .SelectMany(static mapping => mapping.Value.Select(effect =>
                (Source: mapping.Key, Effect: effect)))
            .ToHashSet();
        if (values.Any(value => !expectedPairs.Contains(
                (value.SourceSavvy, value.Effect))))
        {
            throw new InvalidOperationException(
                "Pet owner-Merge rates contain an unsupported source/effect mapping.");
        }
    }
}
