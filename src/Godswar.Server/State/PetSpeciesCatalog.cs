using System.Collections.Frozen;

namespace Godswar.Server.State;

internal enum PetFoodKind
{
    Herbivore = 1,
    Carnivore = 2,
    Omnivore = 3
}

internal enum PetEggCatalogStatus
{
    Consistent,
    PayloadTargetsDifferentSpecies,
    Missing
}

internal sealed record PetSpeciesDefinition(
    int Type,
    string DisplayName,
    PetFoodKind FoodKind,
    int StarterSkillId,
    string StarterSkillName,
    IReadOnlyList<int> ClientLifetimeValues,
    uint? EggItemId,
    int? EggDeclaredSpeciesType,
    uint MagicJadeItemId)
{
    public PetEggCatalogStatus EggStatus =>
        EggItemId is null || EggDeclaredSpeciesType is null
            ? PetEggCatalogStatus.Missing
            : EggDeclaredSpeciesType == Type
                ? PetEggCatalogStatus.Consistent
                : PetEggCatalogStatus.PayloadTargetsDifferentSpecies;
}

/// <summary>
/// Pet species facts transcribed from the stock English client's Pet_Confect.xml,
/// Message_Pet.dat, EquipName.dat, and ItemBaseAttribute.xml.
///
/// Lifetime values retain the client's raw unit because the client does not
/// identify that unit. Egg IDs describe the item whose display name belongs to
/// the species; EggDeclaredSpeciesType records the actual ItemBaseAttribute
/// payload, including the stock client's late-catalog inconsistencies.
/// </summary>
internal static class PetSpeciesCatalog
{
    public const int SpeciesCount = 45;
    public const int EggHatchRuntimeSkillId = 4740;

    public static IReadOnlyList<PetSpeciesDefinition> All { get; } = CreateAll();

    public static IReadOnlyList<PetSpeciesDefinition> EggInconsistencies { get; } =
        All.Where(static species => species.EggStatus != PetEggCatalogStatus.Consistent)
            .ToArray();

    private static readonly FrozenDictionary<int, PetSpeciesDefinition> ByType =
        All.ToFrozenDictionary(static species => species.Type);

    public static bool TryGet(int type, out PetSpeciesDefinition species) =>
        ByType.TryGetValue(type, out species!);

    private static IReadOnlyList<PetSpeciesDefinition> CreateAll()
    {
        var definitions = new PetSpeciesDefinition[]
        {
            D(1, "Rock Elf", PetFoodKind.Omnivore, 405, "Life Totem I", 10150, 1, 11050, 600, 800, 1100),
            D(2, "Flower Pixie", PetFoodKind.Herbivore, 805, "Pixie Dust I", 10151, 2, 11051, 400, 500, 700, 800, 1000),
            D(3, "Minotaur", PetFoodKind.Carnivore, 605, "Tear I", 10152, 3, 11052, 400, 500, 700, 800, 1000),
            D(4, "Panda", PetFoodKind.Omnivore, 2005, "Extraction I", 10153, 4, 11053, 1200),
            D(5, "Easter Bunny", PetFoodKind.Herbivore, 1005, "Concentration I", 10154, 5, 11054, 900, 1500),
            D(6, "Puppet", PetFoodKind.Herbivore, 705, "Immortal Kiss I", 10155, 6, 11055, 500, 1000),
            D(7, "Wing Race", PetFoodKind.Omnivore, 608, "Feather Blade I", 10156, 7, 11056, 600),
            D(8, "Ghost", PetFoodKind.Carnivore, 808, "Dark Vengeance I", 10157, 8, 11057, 1500),
            D(9, "Merman", PetFoodKind.Omnivore, 2700, "Ocean Sphere I", 10158, 9, 11058, 1200),
            D(10, "Loyal Dog", PetFoodKind.Carnivore, 1205, "Guard I", 10159, 10, 11059, 500, 700, 1000),
            D(11, "Tiger Baby", PetFoodKind.Carnivore, 2711, "Tiger's Roar I", 10160, 11, 11060, 1500),
            D(12, "Blue Crystal Dragon", PetFoodKind.Carnivore, 2800, "Iceshot I", 10161, 12, 11061, 1500),
            D(13, "Dodo", PetFoodKind.Herbivore, 2900, "Eagle Eye I", 10162, 13, 11062, 1200),
            D(14, "Elf Guardian", PetFoodKind.Omnivore, 3000, "Magic Barrier I", 10163, 14, 11063, 1200),
            D(15, "Wandering Spirit", PetFoodKind.Omnivore, 3100, "Evasion I", 10164, 15, 11064, 1200),
            D(16, "Young Yeti", PetFoodKind.Herbivore, 454, "Frozen Blessing I", 10165, 16, 11065, 1200),
            D(17, "Sphinx", PetFoodKind.Carnivore, 1930, "Sphinx's Enigma I", 10166, 17, 11066, 1200),
            D(18, "Lil QT", PetFoodKind.Herbivore, 530, "Mind Refresh I", 10167, 18, 11067, 1200),
            D(19, "Impi", PetFoodKind.Omnivore, 3124, "Imp Trick I", 10168, 19, 11068, 1200),
            D(20, "Hell Hound", PetFoodKind.Carnivore, 3148, "Mean Streak I", 10169, 20, 11069, 1200),
            D(21, "Troodon", PetFoodKind.Carnivore, 3172, "Primal Spirit I", 10170, 21, 11070, 1200),
            D(22, "Poison Cactus", PetFoodKind.Omnivore, 3300, "Prick I", 10171, 22, 11071, 1200),
            D(23, "Angelic", PetFoodKind.Omnivore, 3500, "Penalty of Justice I", 10172, 23, 11072, 1200),
            D(24, "Kung-Fu Kenny", PetFoodKind.Omnivore, 3700, "Palm Sweep I", 10173, 24, 11073, 1200),
            D(25, "Cretan Bull", PetFoodKind.Herbivore, 3900, "Wild Bump I", 10174, 25, 11074, 1200),
            D(26, "Gryphon", PetFoodKind.Carnivore, 4100, "Fury of Justice I", 10175, 26, 11075, 1200),
            D(27, "Jungle Boar", PetFoodKind.Herbivore, 4300, "Gnarl I", 10176, 27, 11076, 1200),
            D(28, "Spirit Cat", PetFoodKind.Carnivore, 4400, "Spirit Strength I", 10177, 28, 11077, 1200),
            D(29, "Totoro", PetFoodKind.Herbivore, 4500, "Wild Strength I", 10178, 29, 11078, 1200),
            D(30, "Fox Spirit", PetFoodKind.Omnivore, 4700, "Mesmerise I", 10179, 30, 11079, 1200),
            D(31, "Platypus", PetFoodKind.Carnivore, 4600, "Focus I", 10180, 31, 11080, 1200),
            D(32, "Hops", PetFoodKind.Carnivore, 5100, "Ward I", 10181, 32, 11081, 1200),
            D(33, "Monkey", PetFoodKind.Omnivore, 4800, "Bullseye I", 10182, 33, 11082, 1200),
            D(34, "Mouse", PetFoodKind.Omnivore, 4900, "Scurry I", 10183, 34, 11083, 1200),
            D(35, "Maneater Flower", PetFoodKind.Omnivore, 5300, "Magic Strength I", 10184, 35, 11084, 1200),
            D(36, "Penguin", PetFoodKind.Herbivore, 5000, "Block I", 10185, 36, 11085, 1200),
            D(37, "King Lion", PetFoodKind.Carnivore, 5200, "Violent Strength I", 10186, 37, 11086, 1200),
            D(38, "Thunder Pixie", PetFoodKind.Herbivore, 5400, "Discharge I", 10187, 36, 11087, 1200),
            D(39, "Bloodmoon Fox", PetFoodKind.Carnivore, 5500, "Eclipse I", 10188, 37, 11088, 1200),
            D(40, "Kratortle", PetFoodKind.Carnivore, 5600, "Resolute Physique I", 10189, 38, 11089, 1200),
            D(41, "Beelzeebub", PetFoodKind.Carnivore, 6100, "Magission I", 10190, 39, 11090, 1200),
            D(42, "Billy Bear", PetFoodKind.Omnivore, 6200, "Sacrifice I", 10191, 42, 11091, 1200),
            D(43, "Roly Poly", PetFoodKind.Herbivore, 6300, "Lifedrain I", 10192, 43, 11092, 1200),
            D(44, "Hedgehog", PetFoodKind.Carnivore, 6000, "Spiky Armor I", 10193, 44, 11093, 1200),
            D(45, "Cupid", PetFoodKind.Carnivore, 6000, "Spiky Armor I", null, null, 11094, 1200)
        };

        if (definitions.Length != SpeciesCount ||
            definitions.Where((definition, index) => definition.Type != index + 1).Any() ||
            definitions.Select(static definition => definition.MagicJadeItemId).Distinct().Count() != SpeciesCount)
        {
            throw new InvalidDataException("The client pet-species catalog is incomplete or internally ambiguous.");
        }

        return Array.AsReadOnly(definitions);
    }

    private static PetSpeciesDefinition D(
        int type,
        string name,
        PetFoodKind food,
        int starterSkillId,
        string starterSkillName,
        uint? eggItemId,
        int? eggDeclaredSpeciesType,
        uint magicJadeItemId,
        params int[] lifetimeValues) =>
        new(
            type,
            name,
            food,
            starterSkillId,
            starterSkillName,
            Array.AsReadOnly(lifetimeValues),
            eggItemId,
            eggDeclaredSpeciesType,
            magicJadeItemId);
}
