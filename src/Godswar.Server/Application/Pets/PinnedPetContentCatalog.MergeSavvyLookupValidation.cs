namespace Godswar.Server.Application.Pets;

internal sealed partial class PinnedPetContentCatalog
{
    private const int MergeSavvyLookupRowCount = 200;

    private static void ValidateMergeSavvyLookup(
        PetMergeSavvyLookupContentDefinition[] lookup)
    {
        if (lookup.Length != MergeSavvyLookupRowCount ||
            lookup[0].MinimumSavvyDifference != -4000 ||
            lookup[^1].MinimumSavvyDifference != 800 ||
            lookup[0].BaseIncrease != 1 ||
            lookup[^1].BaseIncrease != 300 ||
            lookup.Select(static value => value.MinimumSavvyDifference)
                .Distinct().Count() != lookup.Length ||
            lookup.Select(static value => value.BaseIncrease)
                .Distinct().Count() != lookup.Length ||
            lookup.Zip(lookup.Skip(1), static (left, right) =>
                    right.MinimumSavvyDifference >
                        left.MinimumSavvyDifference &&
                    right.BaseIncrease > left.BaseIncrease)
                .Any(static value => !value))
        {
            throw new InvalidOperationException(
                "Published pet Merge savvy lookup is incomplete or ambiguous.");
        }

        // Runtime consumes database rows. This compiled sentinel prevents an
        // unreviewed publication from changing the exact 200 Pet_Alter.xml
        // Restrict/Values pairs recovered from the installed client.
        for (var index = 0; index < lookup.Length; index++)
        {
            var order = index + 1;
            var expected = new PetMergeSavvyLookupContentDefinition(
                InstalledClientSavvyThreshold(order),
                checked((ushort)(order <= 100
                    ? order
                    : order * 2 - 100)));
            if (lookup[index] != expected)
            {
                throw new InvalidOperationException(
                    "Published pet Merge savvy lookup diverges from the " +
                    "reviewed installed-client baseline.");
            }
        }
    }

    private static int InstalledClientSavvyThreshold(int order) =>
        order switch
        {
            < 1 or > MergeSavvyLookupRowCount =>
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
