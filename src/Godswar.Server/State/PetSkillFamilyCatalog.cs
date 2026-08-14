using System.Collections.Frozen;

namespace Godswar.Server.State;

internal enum PetSkillFamilyKind
{
    SharedOwnerBonus,
    SpeciesExclusive,
    Savvy
}

internal sealed record PetSkillFamilyDefinition(
    int InitialRuntimeSkillId,
    string DisplayName,
    PetSkillFamilyKind Kind,
    bool HasSkillBooks);

/// <summary>
/// The 67 named families represented by the installed client's 1,655
/// Pet_Skill.xml rows. Runtime IDs and skill-book item IDs are deliberately
/// separate namespaces; only 58 families have books in item range 10200-10745.
/// </summary>
internal static class PetSkillFamilyCatalog
{
    public const int FamilyCount = 67;
    public const int BookBackedFamilyCount = 58;
    public const int RuntimeRowCount = 1655;

    public static IReadOnlyList<PetSkillFamilyDefinition> All { get; } =
        Array.AsReadOnly(
        new PetSkillFamilyDefinition[]
        {
            S(400, "Vital Boost"), X(405, "Life Totem"),
            X(454, "Frozen Blessing"), S(500, "Meditate"),
            X(530, "Mind Refresh", false), S(600, "Sharp Claw"),
            X(605, "Tear"), X(608, "Feather Blade"),
            S(700, "Holy Shield"), X(705, "Immortal Kiss"),
            S(800, "Mystic Oracle"), X(805, "Pixie Dust"),
            X(808, "Dark Vengeance"), S(900, "Sparkling Fog"),
            S(1000, "Death Spike"), X(1005, "Concentration"),
            S(1100, "Brace"), S(1200, "Force Shield"),
            X(1205, "Guard"), S(1300, "Power Surge"),
            S(1400, "Resistance"), S(1500, "Solidify"),
            S(1600, "Mentality"), S(1700, "Wind Ward"),
            S(1800, "Light Ward"), S(1900, "Heart Ward"),
            X(1930, "Sphinx's Enigma"), S(2000, "Blood Chant"),
            X(2005, "Extraction"), V(2100, "Agility"),
            V(2200, "Strength"), V(2300, "Accuracy"),
            V(2400, "Technique"), V(2500, "Wisdom"),
            V(2600, "Luck"), X(2700, "Ocean Sphere"),
            X(2711, "Tiger's Roar"), X(2800, "Iceshot"),
            X(2900, "Eagle Eye"), X(3000, "Magic Barrier"),
            X(3100, "Evasion"), X(3124, "Imp Trick", false),
            X(3148, "Mean Streak", false),
            X(3172, "Primal Spirit", false),
            X(3300, "Prick", false),
            X(3500, "Penalty of Justice", false),
            X(3700, "Palm Sweep", false),
            X(3900, "Wild Bump"),
            X(4100, "Fury of Justice", false),
            X(4300, "Gnarl", false),
            X(4400, "Spirit Strength"), X(4500, "Wild Strength"),
            X(4600, "Focus"), X(4700, "Mesmerise"),
            X(4800, "Bullseye"), X(4900, "Scurry"),
            X(5000, "Block"), X(5100, "Ward"),
            X(5200, "Violent Strength"), X(5300, "Magic Strength"),
            X(5400, "Discharge"), X(5500, "Eclipse"),
            X(5600, "Resolute Physique"), X(6000, "Spiky Armor"),
            X(6100, "Magission"), X(6200, "Sacrifice"),
            X(6300, "Lifedrain")
        });

    private static readonly FrozenDictionary<int, PetSkillFamilyDefinition>
        ByInitialRuntimeSkillId =
            All.ToFrozenDictionary(static family => family.InitialRuntimeSkillId);

    static PetSkillFamilyCatalog()
    {
        if (All.Count != FamilyCount ||
            All.Count(static family => family.HasSkillBooks) != BookBackedFamilyCount)
        {
            throw new InvalidDataException(
                "The installed-client pet skill-family catalog is incomplete.");
        }
    }

    public static bool TryGetByInitialRuntimeSkillId(
        int skillId,
        out PetSkillFamilyDefinition family) =>
        ByInitialRuntimeSkillId.TryGetValue(skillId, out family!);

    private static PetSkillFamilyDefinition S(int id, string name) =>
        new(id, name, PetSkillFamilyKind.SharedOwnerBonus, true);

    private static PetSkillFamilyDefinition X(
        int id,
        string name,
        bool hasBooks = true) =>
        new(id, name, PetSkillFamilyKind.SpeciesExclusive, hasBooks);

    private static PetSkillFamilyDefinition V(int id, string name) =>
        new(id, name, PetSkillFamilyKind.Savvy, true);
}
