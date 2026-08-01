using Godswar.Server.Application.Items;

namespace Godswar.Server.Application.World;

/// <summary>
/// Validates references that cross independently published content families.
/// A combined fingerprint identifies a pair of releases; it cannot make an
/// incompatible pair safe.
/// </summary>
internal static class RuntimeContentCompatibilityValidator
{
    public static void Validate(
        IItemTemplateCatalog items,
        GameplayContentCatalog gameplay)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(gameplay);

        var skills = gameplay.SkillCombatDefinitions.ToDictionary(
            static skill => skill.SkillId);
        foreach (var book in gameplay.SkillBooks)
        {
            if (!items.TryGet(checked((uint)book.ItemId), out var item))
            {
                throw Incompatible(
                    $"skill-book item {book.ItemId} is absent");
            }

            if (!skills.TryGetValue(book.SkillId, out var skill))
            {
                throw Incompatible(
                    $"skill {book.SkillId} for item {book.ItemId} is absent");
            }

            if (!string.Equals(
                    item.NameKey,
                    book.NameKey,
                    StringComparison.Ordinal) ||
                item.MinLevel != book.MinLevel ||
                item.MaxLevel != book.MaxLevel ||
                !SameClasses(item.ClassIds, book.ClassIds))
            {
                throw Incompatible(
                    $"item metadata for skill-book {book.ItemId} differs");
            }

            if (!string.Equals(
                    skill.BaseName,
                    book.BaseName,
                    StringComparison.Ordinal) ||
                skill.SkillLevel != book.SkillLevel ||
                skill.PreviousSkillId != book.PreviousSkillId ||
                skill.MinLevel != book.MinLevel ||
                skill.MaxLevel != book.MaxLevel ||
                !SameClasses(skill.ClassIds, book.ClassIds))
            {
                throw Incompatible(
                    $"skill metadata for skill-book {book.ItemId} differs");
            }
        }
    }

    private static bool SameClasses(
        IReadOnlyList<short> left,
        IReadOnlyList<short> right) =>
        left.Count == right.Count &&
        left.Order().SequenceEqual(right.Order());

    private static InvalidOperationException Incompatible(string reason) =>
        new(
            "Published item and gameplay content revisions are " +
            $"incompatible: {reason}.");
}
