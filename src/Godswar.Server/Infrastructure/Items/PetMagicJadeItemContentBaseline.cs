using System.Globalization;
using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Stock-client Magic Jade inventory definitions. These templates publish the
/// item identities required by the character-items foreign key; consuming a
/// jade and changing a pet species remain intentionally separate gameplay.
/// </summary>
internal static class PetMagicJadeItemContentBaseline
{
    public const string Icon = "396,756";
    public const short StackCap = 99;

    private static readonly string[] StockDisplayNames =
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

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        Create();

    private static IReadOnlyList<ItemTemplateSeed> Create()
    {
        if (StockDisplayNames.Length != PetSpeciesCatalog.SpeciesCount)
        {
            throw new InvalidDataException(
                "The stock Magic Jade item-name catalog is incomplete.");
        }

        var templates = PetSpeciesCatalog.All
            .OrderBy(static species => species.MagicJadeItemId)
            .Select((species, index) => Create(species, StockDisplayNames[index]))
            .ToArray();
        if (!templates.Select(static item => item.Id)
                .SequenceEqual(Enumerable.Range(11050, 45)))
        {
            throw new InvalidDataException(
                "The stock Magic Jade item identities are not contiguous.");
        }

        return templates;
    }

    private static ItemTemplateSeed Create(
        PetSpeciesDefinition species,
        string displayName)
    {
        var itemId = checked((int)species.MagicJadeItemId);
        var nameKey = $"Pet{itemId.ToString(CultureInfo.InvariantCulture)}";
        var stats = new Dictionary<string, string>
        {
            ["ID"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["Type"] = PetItemContentBaseline.ItemType,
            ["Texture"] = PetItemContentBaseline.Texture,
            ["Icon"] = Icon,
            ["Random"] = "0",
            ["Distribution"] = "0,0",
            ["Money"] = "0",
            ["Overlap"] = StackCap.ToString(CultureInfo.InvariantCulture)
        };

        return new ItemTemplateSeed(
            itemId,
            PetItemContentBaseline.ItemType,
            nameKey,
            displayName,
            EquipmentSlot: 0,
            ClassIds: [],
            MinLevel: null,
            MaxLevel: null,
            Hand: null,
            SkillFlag: null,
            PetItemContentBaseline.Texture,
            Icon,
            JsonSerializer.Serialize(stats));
    }
}
