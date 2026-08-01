using Godswar.Server.Application.Items;
using Godswar.Server.Application.World;

namespace Godswar.Server.ProtocolChecks;

internal static class RuntimeContentCompatibilityChecks
{
    public const string CheckName =
        "Cross-family runtime-content compatibility";

    public static Task RunAsync()
    {
        var skill = new GameplaySkillCombatDefinition(
            42,
            0,
            0,
            1,
            1,
            0,
            0,
            1,
            1,
            TimeSpan.Zero,
            TimeSpan.Zero)
        {
            DisplayName = "Skill",
            BaseName = "Skill",
            SkillLevel = 1,
            ClassIds = new short[] { 1 },
            MinLevel = 10,
            MaxLevel = 120
        };
        var book = new GameplaySkillBookDefinition(
            5000,
            "SkillBook",
            "Book: Skill",
            skill.SkillId,
            skill.BaseName,
            skill.SkillLevel,
            skill.ClassIds,
            skill.MinLevel,
            skill.MaxLevel,
            skill.PreviousSkillId,
            "{}");
        var gameplay = GameplayContentCatalog.Empty with
        {
            SkillCombatDefinitions = new[] { skill },
            SkillBooks = new[] { book }
        };
        var compatible = Catalog(book);
        RuntimeContentCompatibilityValidator.Validate(
            compatible,
            gameplay);

        var incompatible = Catalog(book with { MinLevel = 11 });
        Check.Throws<InvalidOperationException>(
            () => RuntimeContentCompatibilityValidator.Validate(
                incompatible,
                gameplay),
            "independently advanced incompatible item pointer is rejected");
        return Task.CompletedTask;
    }

    private static PinnedItemTemplateCatalog Catalog(
        GameplaySkillBookDefinition book) =>
        PinnedItemTemplateCatalog.Create(
            "compatibility-check",
            new[]
            {
                new ItemTemplateDefinition(
                    checked((uint)book.ItemId),
                    "consume item",
                    book.NameKey,
                    book.DisplayName,
                    -1,
                    book.ClassIds,
                    book.MinLevel,
                    book.MaxLevel,
                    null,
                    null,
                    string.Empty,
                    string.Empty,
                    "{}")
            });
}
