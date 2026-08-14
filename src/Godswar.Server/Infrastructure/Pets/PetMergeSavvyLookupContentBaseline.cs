using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

/// <summary>
/// Exact installed-client Pet_Alter.xml Restrict/Values merge-savvy rows.
/// Used only to publish an immutable database revision.
/// </summary>
internal static class PetMergeSavvyLookupContentBaseline
{
    private const int LookupRowCount = 200;

    public static PetMergeSavvyLookupContentDefinition[] Create() =>
        Enumerable.Range(1, LookupRowCount)
            .Select(static order =>
                new PetMergeSavvyLookupContentDefinition(
                    Restrict(order),
                    checked((ushort)(order <= 100
                        ? order
                        : order * 2 - 100))))
            .ToArray();

    private static int Restrict(int order) =>
        order switch
        {
            < 1 or > LookupRowCount =>
                throw new ArgumentOutOfRangeException(nameof(order)),
            <= 10 => checked(-4000 + (order - 1) * 10),
            <= 16 => checked(-3910 + (order - 10) * 12),
            <= 22 => checked(-3838 + (order - 16) * 14),
            <= 28 => checked(-3754 + (order - 22) * 16),
            <= 34 => checked(-3658 + (order - 28) * 18),
            <= 40 => checked(-3550 + (order - 34) * 20),
            <= 45 => checked(-3430 + (order - 40) * 22),
            <= 50 => checked(-3320 + (order - 45) * 24),
            <= 125 => checked(-3200 + (order - 50) * 32),
            <= 175 => checked(-800 + (order - 125) * 24),
            _ => checked(400 + (order - 175) * 16)
        };
}
