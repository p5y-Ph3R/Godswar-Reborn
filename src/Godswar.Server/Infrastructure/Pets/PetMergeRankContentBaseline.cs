using Godswar.Server.Application.Pets;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Pets;

/// <summary>
/// Reviewed stock Pet_Alter Qualityadd, pet-type, and spirit-effectiveness
/// declarations. These are used only to publish an immutable database revision.
/// </summary>
internal static class PetMergeRankContentBaseline
{
    private const int LookupRowCount = 200;

    public static PetMergeRankLookupContentDefinition[] CreateLookup() =>
        Enumerable.Range(1, LookupRowCount)
            .Select(static order => new PetMergeRankLookupContentDefinition(
                QualityAddThreshold(order),
                checked((ushort)(order <= 100
                    ? order
                    : order * 2 - 100))))
            .ToArray();

    public static PetMergeRankSpeciesFactorContentDefinition[]
        CreateSpeciesFactors() =>
        PetSpeciesCatalog.All
            .OrderBy(static value => value.Type)
            .Select(static value =>
                new PetMergeRankSpeciesFactorContentDefinition(
                    checked((short)value.Type),
                    value.Type switch
                    {
                        2 or 3 or 6 or 10 => 0.8m,
                        1 or 7 => 1.4m,
                        _ => 2.6m
                    }))
            .ToArray();

    public static PetMergeRankSpiritStepContentDefinition[]
        CreateSpiritSteps() =>
        Enumerable.Range(0, PetManagerPlanner.MaximumSpiritItems + 1)
            .Select(static spiritCount =>
                new PetMergeRankSpiritStepContentDefinition(
                    checked((short)spiritCount),
                    checked((short)(spiritCount * 10)),
                    100))
            .ToArray();

    private static int QualityAddThreshold(int order) =>
        order switch
        {
            < 1 or > LookupRowCount => throw new ArgumentOutOfRangeException(
                nameof(order)),
            <= 49 => checked(-3000 + (order - 1) * 12),
            <= 100 => checked(-2400 + (order - 50) * 12),
            <= 175 => checked(-1800 + (order - 100) * 24),
            _ => checked((order - 175) * 20)
        };
}
