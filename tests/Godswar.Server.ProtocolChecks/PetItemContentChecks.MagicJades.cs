using System.Text.Json;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetItemContentChecks
{
    private static string[] StockMagicJadeDisplayNames =>
    [
        "Magic Jade: Rock Elf",
        "Magic Jade:Flower Pixie",
        "Magic Jade : Minotaur ",
        "Magic Jade : Panda",
        "Magic Jade: Easter Bunny",
        "Magic Jade:PupPet",
        "Magic Jade:Wing Race",
        "Magic Jade: Ghost",
        "Magic Jade: Merman",
        "Magic Jade: Loyal Dog",
        "Magic Jade: Tiger Baby",
        "Magic Jade: Blue Crystal Dragon",
        "Magic Jade: Dodo",
        "Magic Jade: Elf Guardian",
        "Magic Jade: Wandering Spirit",
        "Magic Jade: Young Yeti",
        "Magic Jade: Sphinx",
        "Magic Jade: Lil QT",
        "Magic Jade: Impi",
        "Magic Jade: Hell Hound",
        "Magic Jade: Troodon",
        "Magic Jade: Poison Cactus",
        "Magic Jade: Angelic",
        "Magic Jade: Kung-Fu Kenny",
        "Magic Jade: Cretan Bull",
        "Magic Jade: Gryphon",
        "Magic Jade: Jungle Boar",
        "Magic Jade: Spirit Cat",
        "Magic Jade: Totoro",
        "Magic Jade: Fox Spirit",
        "Magic Jade: Platypus",
        "Magic Jade: Hops",
        "Magic Jade: Monkey",
        "Magic Jade: Mouse",
        "Magic Jade: Maneater Flower",
        "Magic Jade: Penguin",
        "Magic Jade: King Lion",
        "Magic Jade: Thunder Pixie",
        "Magic Jade: Bloodmoon Fox",
        "Magic Jade: Kratortle",
        "Magic Jade: Beelzeebub",
        "Magic Jade: Billy Bear",
        "Magic Jade: Roly Poly",
        "Magic Jade: Hedgehog",
        "Magic Jade: Cupid"
    ];

    private static ExpectedItem[] CreateExpectedMagicJadeItems() =>
        StockMagicJadeDisplayNames
            .Select((name, index) => E(
                11050 + index,
                $"Pet{11050 + index}",
                name,
                PetMagicJadeItemContentBaseline.Icon,
                "99"))
            .ToArray();

    private static void CheckMagicJadeItems(
        IReadOnlyList<ItemTemplateSeed> seeds)
    {
        var jades = seeds
            .Where(static item => item.Id is >= 11050 and <= 11094)
            .OrderBy(static item => item.Id)
            .ToArray();
        Check.True(
            jades.Length == PetSpeciesCatalog.SpeciesCount &&
            jades.Select(static item => item.Id)
                .SequenceEqual(Enumerable.Range(11050, 45)) &&
            jades.Select(static item => item.DisplayName)
                .SequenceEqual(StockMagicJadeDisplayNames),
            "all stock Magic Jade inventory identities and names are reviewed");

        foreach (var jade in jades)
        {
            using var document = JsonDocument.Parse(jade.StatsJson);
            var stats = document.RootElement;
            Check.True(
                jade.NameKey == $"Pet{jade.Id}" &&
                jade.Icon == "396,756" &&
                stats.GetProperty("Overlap").GetString() == "99" &&
                stats.EnumerateObject().Count() == 8,
                $"Magic Jade {jade.Id} retains exact inert stock item metadata");
        }

        Check.True(
            jades[5].DisplayName == "Magic Jade:PupPet" &&
            PetSpeciesCatalog.All[5].DisplayName == "Puppet",
            "Magic Jade 11055 preserves the stock item-name typo without " +
            "changing canonical species naming");
    }
}
