using System.Text.Json;

namespace Godswar.Server.State;

internal static class SkillBookItemTemplateProjection
{
    public static ItemTemplateSeed ToItemTemplateSeed(
        this SkillBookTemplateSeed seed)
    {
        using var document = JsonDocument.Parse(seed.StatsJson);
        var root = document.RootElement;
        return new ItemTemplateSeed(
            seed.ItemId,
            "consume item",
            seed.NameKey,
            seed.DisplayName,
            -1,
            seed.ClassIds.ToArray(),
            seed.MinLevel,
            seed.MaxLevel,
            null,
            null,
            ReadString(root, "Texture"),
            ReadString(root, "Icon"),
            seed.StatsJson);
    }

    private static string ReadString(
        JsonElement root,
        string property) =>
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
