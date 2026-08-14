namespace Godswar.Server.ProtocolChecks;

internal static partial class PetItemContentChecks
{
    private static ExpectedItem[] CreateExpectedPetSkillBookItems() =>
    [
        .. Family(10464, ["Pet Skill:Wild Bump I", "Pet Skill:Wild Bump II", "Pet Skill:Wild Bump III", "Pet Skill:Wild Bump IV", "Pet Skill:Wild Bump V", "Pet Skill:Wild Bump VI"], [3900, 3904, 3908, 3912, 3916, 3920]),
        .. Family(10510, ["Pet Skill: Wild Strength I", "Pet Skill:Wild Strength  II", "Pet Skill:Wild Strength  III", "Pet Skill:Wild Strength  IV", "Pet Skill:Wild Strength  V", "Pet Skill:Wild Strength  VI"], [4500, 4503, 4507, 4511, 4515, 4519]),
        .. Family(10530, ["Pet Skill: Focus  I", "Pet Skill:Focus  II", "Pet Skill:Focus  III", "Pet Skill:Focus  IV", "Pet Skill:Focus  V", "Pet Skill:Focus  VI"], [4600, 4604, 4608, 4612, 4616, 4620]),
        .. Family(10590, ["Pet Skill: Violent Strength I", "Pet Skill:Violent Strength II", "Pet Skill:Violent Strength III", "Pet Skill:Violent Strength IV", "Pet Skill:Violent Strength V", "Pet Skill:Violent Strength VI"], [5200, 5204, 5208, 5212, 5216, 5220]),
        .. Family(10700, ["Pet Skill: Resolute Physique I", "Pet Skill: Resolute Physique II", "Pet Skill: Resolute Physique III", "Pet Skill: Resolute Physique IV", "Pet Skill: Resolute Physique V", "Pet Skill: Resolute Physique VI"], [5600, 5604, 5608, 5612, 5616, 5620])
    ];

    private static IEnumerable<ExpectedItem> Family(
        int firstItemId,
        IReadOnlyList<string> displayNames,
        IReadOnlyList<int> petSkillIds)
    {
        for (var index = 0; index < displayNames.Count; index++)
        {
            var itemId = firstItemId + index;
            yield return E(
                itemId,
                $"Pet{itemId}",
                displayNames[index],
                "216,972",
                "99",
                use: "1",
                itemType: index == 0 ? "4" : "3",
                petSkill: petSkillIds[index].ToString());
        }
    }
}
