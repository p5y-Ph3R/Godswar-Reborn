using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

/// <summary>
/// Server-only bonus codes are deliberately outside the native PetUnite effect
/// enum. They may be materialized in PostgreSQL but must never be serialized as
/// one of the client's sixteen owner-Merge fields.
/// </summary>
internal enum PetOwnerMergeInternalBonusCode : short
{
    TechniquePhysicalReduction = 1001,
    TechniqueMagicReduction = 1002
}

internal readonly record struct PetOwnerMergeStoredBonusValue(
    short Code,
    decimal Value);

internal static class PetOwnerMergeStoredBonusCodec
{
    public static int NativeCount =>
        Enum.GetValues<PetOwnerMergeEffectCode>().Length;

    public static int InternalCount =>
        Enum.GetValues<PetOwnerMergeInternalBonusCode>().Length;

    public static int TotalCount => NativeCount + InternalCount;

    public static IReadOnlyList<PetOwnerMergeStoredBonusValue> ToStoredValues(
        PetOwnerStatContribution contribution)
    {
        if (!contribution.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(contribution));
        }

        return PetOwnerMergeContributionCalculator
            .ToEffectValues(contribution)
            .Select(static value => new PetOwnerMergeStoredBonusValue(
                (short)value.Effect,
                value.Value))
            .Append(new(
                (short)PetOwnerMergeInternalBonusCode
                    .TechniquePhysicalReduction,
                contribution.TechniquePhysicalReduction))
            .Append(new(
                (short)PetOwnerMergeInternalBonusCode
                    .TechniqueMagicReduction,
                contribution.TechniqueMagicReduction))
            .OrderBy(static value => value.Code)
            .ToArray();
    }

    public static PetOwnerStatContribution FromStoredValues(
        IEnumerable<PetOwnerMergeStoredBonusValue> storedValues)
    {
        ArgumentNullException.ThrowIfNull(storedValues);
        var values = new Dictionary<short, decimal>();
        foreach (var value in storedValues)
        {
            if (!IsDefined(value.Code) ||
                value.Value < 0m ||
                !values.TryAdd(value.Code, value.Value))
            {
                throw new InvalidDataException(
                    "Pet owner-Merge stored bonuses are invalid or duplicated.");
            }
        }

        if (values.Count != TotalCount ||
            Enum.GetValues<PetOwnerMergeEffectCode>().Any(
                code => !values.ContainsKey((short)code)) ||
            Enum.GetValues<PetOwnerMergeInternalBonusCode>().Any(
                code => !values.ContainsKey((short)code)))
        {
            throw new InvalidDataException(
                "Pet owner-Merge stored bonuses are incomplete.");
        }

        var native = PetOwnerMergeContributionCalculator.FromEffectValues(
            Enum.GetValues<PetOwnerMergeEffectCode>().Select(code =>
                new PetOwnerMergeEffectValue(
                    code,
                    values[(short)code])));
        return native with
        {
            TechniquePhysicalReduction = values[
                (short)PetOwnerMergeInternalBonusCode
                    .TechniquePhysicalReduction],
            TechniqueMagicReduction = values[
                (short)PetOwnerMergeInternalBonusCode
                    .TechniqueMagicReduction]
        };
    }

    public static bool IsDefined(short code) =>
        Enum.IsDefined((PetOwnerMergeEffectCode)code) ||
        Enum.IsDefined((PetOwnerMergeInternalBonusCode)code);
}
