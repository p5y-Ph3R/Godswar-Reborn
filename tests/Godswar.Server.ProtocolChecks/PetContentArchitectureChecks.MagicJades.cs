using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetContentArchitectureChecks
{
    private static readonly string[] ExpectedMagicJadeAppearances =
    [
        "Rock Elf", "Flower Pixie", "Minotaur", "Panda",
        "Easter Bunny", "Puppet", "Wing Race", "Ghost", "Merman",
        "Loyal Dog", "Tiger Baby", "Blue Crystal Dragon", "Dodo",
        "Elf Guardian", "Wandering Spirit", "Young Yeti", "Sphinx",
        "Lil QT", "Impi", "Hell Hound", "Troodon", "Poison Cactus",
        "Angelic", "Kung-Fu Kenny", "Cretan Bull", "Gryphon",
        "Jungle Boar", "Spirit Cat", "Totoro", "Fox Spirit", "Platypus",
        "Hops", "Monkey", "Mouse", "Maneater Flower", "Penguin",
        "King Lion", "Thunder Pixie", "Bloodmoon Fox", "Kratortle",
        "Beelzeebub", "Billy Bear", "Roly Poly", "Hedgehog", "Cupid"
    ];

    internal static IReadOnlyList<string> ExpectedMagicJadeAppearanceNames =>
        ExpectedMagicJadeAppearances;

    private static void AssertMagicJadeAppearanceGroups(
        PinnedPetContentCatalog baseline)
    {
        var appearances = baseline.Species
            .OrderBy(static value => value.MagicJadeItemId)
            .ToArray();
        Check.True(
            appearances.Length == 45 &&
            appearances.Select(static value => value.MagicJadeItemId)
                .SequenceEqual(Enumerable.Range(11050, 45)
                    .Select(static value => checked((uint)value))) &&
            appearances.Select(static value => value.DisplayName)
                .SequenceEqual(ExpectedMagicJadeAppearances),
            "pet content pins every Magic Jade ID and canonical appearance name");
        foreach (var appearance in appearances)
        {
            Check.True(
                baseline.TryGetSpeciesByMagicJadeItemId(
                    appearance.MagicJadeItemId,
                    out var resolved) &&
                resolved == appearance,
                $"Magic Jade {appearance.MagicJadeItemId} resolves through pinned pet content");
        }

        var factors = baseline.MergeRankSpeciesFactors.ToDictionary(
            static value => value.SpeciesId,
            static value => value.Factor);
        Check.True(
            appearances.Count(value => factors[value.SpeciesId] == 0.8m) == 4 &&
            appearances.Count(value => factors[value.SpeciesId] == 1.4m) == 2 &&
            appearances.Count(value => factors[value.SpeciesId] == 2.6m) == 39,
            "Magic Jade appearances retain the 2.40/4.20/7.80 cap groups");
    }
}
