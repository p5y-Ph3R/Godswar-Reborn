using System.Text.Json;

namespace Godswar.Server.Application.World;

internal sealed partial class PinnedWorldContentReader
{
    private const int MaximumGameplayClasses = 128;
    private const int MaximumGameplayTalents = 100_000;
    private const int MaximumGameplaySkillBooks = 100_000;
    private const int MaximumGameplayDescriptionLength = 16_384;
    private const int MaximumGameplayJsonLength = 65_536;

    private static PinnedGameplayProgression PinGameplayProgression(
        GameplayContentCatalog content)
    {
        var classes = MaterializeGameplay(
                content.Classes,
                MaximumGameplayClasses,
                "class definition")
            .OrderBy(static value => value.Id)
            .ToArray();
        var classIds = new HashSet<short>();
        foreach (var value in classes)
        {
            if (value.Id < 0 ||
                !classIds.Add(value.Id) ||
                !IsGameplayText(value.Name, 32) ||
                !IsGameplayText(value.DisplayName, 64) ||
                !IsOptionalGameplayText(value.Source, 128))
            {
                throw Invalid("gameplay", "A gameplay class is invalid.");
            }
        }

        var effects = MaterializeGameplay(
                content.TalentEffects,
                MaximumGameplayTalents,
                "talent-effect definition")
            .OrderBy(static value => value.Id)
            .ToArray();
        var effectIds = new HashSet<short>();
        foreach (var value in effects)
        {
            if (value.Id < 0 ||
                !effectIds.Add(value.Id) ||
                !IsGameplayText(value.Key, 32) ||
                !IsGameplayText(value.DisplayName, 128))
            {
                throw Invalid(
                    "gameplay",
                    "A gameplay talent effect is invalid.");
            }
        }

        var talents = MaterializeGameplay(
                content.Talents,
                MaximumGameplayTalents,
                "talent definition")
            .OrderBy(static value => value.Id)
            .ToArray();
        var talentIds = new HashSet<int>();
        foreach (var value in talents)
        {
            if (value.Id < 0 ||
                value.TreeOrder < 0 ||
                !talentIds.Add(value.Id) ||
                !classIds.Contains(value.ClassId) ||
                !effectIds.Contains(value.EffectId) ||
                !IsGameplayText(value.Name, 128) ||
                !IsGameplayText(value.EffectType, 32) ||
                value.RequiredPrefixRank < 0 ||
                value.RequiredTotalRank < 0 ||
                value.IconWidth < 0 ||
                value.IconHeight < 0 ||
                !IsValidJson(value.StatsJson))
            {
                throw Invalid("gameplay", "A gameplay talent is invalid.");
            }
        }

        var skills = MaterializeGameplay(
                content.SkillCombatDefinitions,
                MaximumGameplaySkills,
                "skill definition")
            .Select(static value => value with
            {
                ClassIds = Array.AsReadOnly(
                    value.ClassIds.Order().ToArray())
            })
            .OrderBy(static value => value.SkillId)
            .ToArray();
        var skillIds = new HashSet<int>();
        foreach (var value in skills)
        {
            if (value.SkillId < 0 ||
                !skillIds.Add(value.SkillId) ||
                value.ClassIds.Any(classId => !classIds.Contains(classId)) ||
                value.ClassIds.Distinct().Count() != value.ClassIds.Count ||
                !IsGameplayText(value.DisplayName, 128) ||
                !IsGameplayText(value.BaseName, 128) ||
                !IsOptionalGameplayText(
                    value.Description,
                    MaximumGameplayDescriptionLength) ||
                !float.IsFinite(value.Distance) ||
                !float.IsFinite(value.Range) ||
                value.CastTime < TimeSpan.Zero ||
                value.Cooldown < TimeSpan.Zero ||
                value.CastTime > TimeSpan.FromHours(1) ||
                value.Cooldown > TimeSpan.FromDays(30) ||
                !IsValidJson(value.StatsJson))
            {
                throw Invalid("gameplay", "A gameplay skill is invalid.");
            }
        }

        foreach (var skill in skills)
        {
            if (skill.PreviousSkillId.HasValue &&
                !skillIds.Contains(skill.PreviousSkillId.Value))
            {
                throw Invalid(
                    "gameplay",
                    "A gameplay skill predecessor is missing.");
            }
        }

        var books = MaterializeGameplay(
                content.SkillBooks,
                MaximumGameplaySkillBooks,
                "skill-book definition")
            .Select(static value => value with
            {
                ClassIds = Array.AsReadOnly(
                    value.ClassIds.Order().ToArray())
            })
            .OrderBy(static value => value.ItemId)
            .ToArray();
        var bookIds = new HashSet<int>();
        foreach (var value in books)
        {
            if (value.ItemId <= 0 ||
                !bookIds.Add(value.ItemId) ||
                !skillIds.Contains(value.SkillId) ||
                value.ClassIds.Any(classId => !classIds.Contains(classId)) ||
                value.ClassIds.Distinct().Count() != value.ClassIds.Count ||
                !IsGameplayText(value.NameKey, 128) ||
                !IsGameplayText(value.DisplayName, 128) ||
                !IsGameplayText(value.BaseName, 128) ||
                !IsValidJson(value.StatsJson))
            {
                throw Invalid("gameplay", "A gameplay skill book is invalid.");
            }
        }

        return new PinnedGameplayProgression(
            classes,
            effects,
            talents,
            skills,
            books);
    }

    private static bool IsValidJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumGameplayJsonLength)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record PinnedGameplayProgression(
        GameplayClassDefinition[] Classes,
        GameplayTalentEffectDefinition[] TalentEffects,
        GameplayTalentDefinition[] Talents,
        GameplaySkillCombatDefinition[] Skills,
        GameplaySkillBookDefinition[] SkillBooks);
}
