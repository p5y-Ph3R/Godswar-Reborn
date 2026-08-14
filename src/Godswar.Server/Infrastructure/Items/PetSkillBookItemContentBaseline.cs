using System.Globalization;
using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// The reviewed stock-client pet-skill book families available to the
/// authoritative activation path. Each family contains ranks I-VI.
/// </summary>
internal static class PetSkillBookItemContentBaseline
{
    private const string ItemType = "consume item";
    private const string Texture =
        "./Localization/en_us/UI/Texture/Icon2.gwo";
    private const string Icon = "216,972";

    private static readonly ReviewedFamily[] Families =
    [
        new(
            10464,
            [
                "Pet Skill:Wild Bump I",
                "Pet Skill:Wild Bump II",
                "Pet Skill:Wild Bump III",
                "Pet Skill:Wild Bump IV",
                "Pet Skill:Wild Bump V",
                "Pet Skill:Wild Bump VI"
            ],
            [3900, 3904, 3908, 3912, 3916, 3920]),
        new(
            10510,
            [
                "Pet Skill: Wild Strength I",
                "Pet Skill:Wild Strength  II",
                "Pet Skill:Wild Strength  III",
                "Pet Skill:Wild Strength  IV",
                "Pet Skill:Wild Strength  V",
                "Pet Skill:Wild Strength  VI"
            ],
            [4500, 4503, 4507, 4511, 4515, 4519]),
        new(
            10530,
            [
                "Pet Skill: Focus  I",
                "Pet Skill:Focus  II",
                "Pet Skill:Focus  III",
                "Pet Skill:Focus  IV",
                "Pet Skill:Focus  V",
                "Pet Skill:Focus  VI"
            ],
            [4600, 4604, 4608, 4612, 4616, 4620]),
        new(
            10590,
            [
                "Pet Skill: Violent Strength I",
                "Pet Skill:Violent Strength II",
                "Pet Skill:Violent Strength III",
                "Pet Skill:Violent Strength IV",
                "Pet Skill:Violent Strength V",
                "Pet Skill:Violent Strength VI"
            ],
            [5200, 5204, 5208, 5212, 5216, 5220]),
        new(
            10700,
            [
                "Pet Skill: Resolute Physique I",
                "Pet Skill: Resolute Physique II",
                "Pet Skill: Resolute Physique III",
                "Pet Skill: Resolute Physique IV",
                "Pet Skill: Resolute Physique V",
                "Pet Skill: Resolute Physique VI"
            ],
            [5600, 5604, 5608, 5612, 5616, 5620])
    ];

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        Families.SelectMany(CreateFamily).ToArray();

    private static IEnumerable<ItemTemplateSeed> CreateFamily(
        ReviewedFamily family)
    {
        if (family.DisplayNames.Count != 6 || family.PetSkillIds.Count != 6)
        {
            throw new InvalidDataException(
                $"Pet skill-book family {family.FirstItemId} is incomplete.");
        }

        for (var index = 0; index < family.PetSkillIds.Count; index++)
        {
            var itemId = family.FirstItemId + index;
            var rank = checked((short)(index + 1));
            var stats = new Dictionary<string, string>
            {
                ["ID"] = itemId.ToString(CultureInfo.InvariantCulture),
                ["Type"] = ItemType,
                ["Texture"] = Texture,
                ["Icon"] = Icon,
                ["Random"] = "0",
                ["Distribution"] = "0,0",
                ["Money"] = "0",
                ["Overlap"] = "99",
                ["Use"] = "1",
                ["ItemType"] = rank == 1 ? "4" : "3",
                ["PetSkill"] = family.PetSkillIds[index]
                    .ToString(CultureInfo.InvariantCulture)
            };
            yield return new ItemTemplateSeed(
                itemId,
                ItemType,
                $"Pet{itemId}",
                family.DisplayNames[index],
                EquipmentSlot: 0,
                ClassIds: [],
                MinLevel: null,
                MaxLevel: null,
                Hand: null,
                SkillFlag: null,
                Texture,
                Icon,
                JsonSerializer.Serialize(stats));
        }
    }

    private sealed record ReviewedFamily(
        int FirstItemId,
        IReadOnlyList<string> DisplayNames,
        IReadOnlyList<int> PetSkillIds);
}
