using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal sealed record PetSkillBookActivationDefinition(
    uint ItemId,
    int FamilyType,
    short Priority,
    int RuntimeSkillId,
    PetSkillTraitRequirement TraitRequirement);

/// <summary>
/// Fail-closed allow-list for the stock pet-skill families reviewed for
/// live activation. Item metadata and learned-skill content must agree with
/// this independent mapping before a book can reach durable mutation code.
/// </summary>
internal static class PetSkillBookActivationPolicy
{
    private static readonly FrozenDictionary<
        uint,
        (int RuntimeSkillId, short Priority)> ReviewedBooks =
        new Dictionary<uint, (int, short)>
        {
            [10464] = (3900, 1), [10465] = (3904, 2),
            [10466] = (3908, 3), [10467] = (3912, 4),
            [10468] = (3916, 5), [10469] = (3920, 6),
            [10510] = (4500, 1), [10511] = (4503, 2),
            [10512] = (4507, 3), [10513] = (4511, 4),
            [10514] = (4515, 5), [10515] = (4519, 6),
            [10530] = (4600, 1), [10531] = (4604, 2),
            [10532] = (4608, 3), [10533] = (4612, 4),
            [10534] = (4616, 5), [10535] = (4620, 6),
            [10590] = (5200, 1), [10591] = (5204, 2),
            [10592] = (5208, 3), [10593] = (5212, 4),
            [10594] = (5216, 5), [10595] = (5220, 6),
            [10700] = (5600, 1), [10701] = (5604, 2),
            [10702] = (5608, 3), [10703] = (5612, 4),
            [10704] = (5616, 5), [10705] = (5620, 6)
        }.ToFrozenDictionary();

    public static bool IsReviewedItem(uint itemId) =>
        ReviewedBooks.ContainsKey(itemId);

    public static bool TryResolve(
        IItemTemplateCatalog items,
        IPetLearnedSkillContentCatalog learnedSkills,
        uint itemId,
        out PetSkillBookActivationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(learnedSkills);
        definition = null!;
        if (!ReviewedBooks.TryGetValue(itemId, out var reviewed) ||
            !items.TryGet(itemId, out var item) ||
            !string.Equals(
                item.Kind,
                "consume item",
                StringComparison.Ordinal) ||
            !TryReadReviewedMetadata(
                item.StatsJson,
                checked((int)itemId),
                reviewed.RuntimeSkillId,
                reviewed.Priority) ||
            !learnedSkills.TryGetCurveByRuntimeSkillId(
                reviewed.RuntimeSkillId,
                out var curve) ||
            curve.FirstRuntimeSkillId != reviewed.RuntimeSkillId ||
            curve.Priority != reviewed.Priority)
        {
            return false;
        }

        definition = new(
            itemId,
            curve.FamilyType,
            curve.Priority,
            curve.FirstRuntimeSkillId,
            curve.LearnTraitRequirement);
        return true;
    }

    private static bool TryReadReviewedMetadata(
        string statsJson,
        int expectedItemId,
        int expectedSkillId,
        short expectedPriority)
    {
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            var root = document.RootElement;
            if (!TryReadInt32(root, "ID", out var id) ||
                !TryReadInt32(root, "Use", out var use) ||
                !TryReadInt32(root, "Overlap", out var overlap) ||
                !TryReadInt32(root, "ItemType", out var itemType) ||
                !TryReadInt32(root, "PetSkill", out var skillId) ||
                id != expectedItemId || use != 1 || overlap != 99 ||
                skillId != expectedSkillId)
            {
                return false;
            }

            return itemType == (expectedPriority == 1 ? 4 : 3);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadInt32(
        JsonElement root,
        string property,
        out int value)
    {
        value = 0;
        if (!root.TryGetProperty(property, out var element))
        {
            return false;
        }
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }
}
