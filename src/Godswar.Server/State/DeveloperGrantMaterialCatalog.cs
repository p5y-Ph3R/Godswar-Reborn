namespace Godswar.Server.State;

internal sealed record DeveloperGrantMaterialDefinition(
    uint ItemId,
    string DisplayName,
    short StackCap,
    short GrantedBound);

/// <summary>
/// The complete, closed allowlist for the local developer item command.
/// The command passes only an ID and quantity to the store; this catalog keeps
/// stack limits and binding authoritative on the server.
/// </summary>
internal static class DeveloperGrantMaterialCatalog
{
    public static IReadOnlyList<DeveloperGrantMaterialDefinition> All { get; } = CreateAll();

    private static readonly IReadOnlyDictionary<uint, DeveloperGrantMaterialDefinition> ByItemId =
        All.ToDictionary(static material => material.ItemId);

    private static readonly IReadOnlyDictionary<string, DeveloperGrantMaterialDefinition> ByAlias =
        CreateAliasMap();

    public static bool TryResolve(uint itemId, out DeveloperGrantMaterialDefinition material)
    {
        return ByItemId.TryGetValue(itemId, out material!);
    }

    public static bool TryResolve(string alias, out DeveloperGrantMaterialDefinition material)
    {
        return ByAlias.TryGetValue(NormalizeAlias(alias), out material!);
    }

    private static IReadOnlyList<DeveloperGrantMaterialDefinition> CreateAll()
    {
        return ForgingMaterialCatalog.All
            .Select(static material => new DeveloperGrantMaterialDefinition(
                material.ItemId,
                material.DisplayName,
                material.StackCap,
                material.GrantedBound))
            .Concat(GearEnhancementMaterialCatalog.All.Select(static material =>
                new DeveloperGrantMaterialDefinition(
                    material.ItemId,
                    material.DisplayName,
                    material.StackCap,
                    GrantedBound: 0)))
            .Concat(GearMentorMaterialCatalog.AttributeDusts.Select(static material =>
                new DeveloperGrantMaterialDefinition(
                    material.ItemId,
                    material.DisplayName,
                    material.StackCap,
                    material.GrantedBound)))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, DeveloperGrantMaterialDefinition> CreateAliasMap()
    {
        var aliases = new Dictionary<string, DeveloperGrantMaterialDefinition>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var source in ForgingMaterialCatalog.All)
        {
            var material = ByItemId[source.ItemId];
            foreach (var alias in ForgingMaterialCatalog.GetAliases(source))
            {
                AddAlias(aliases, alias, material);
            }
        }

        foreach (var source in GearEnhancementMaterialCatalog.All)
        {
            var material = ByItemId[source.ItemId];
            AddAlias(aliases, source.NameKey, material);
            AddAlias(aliases, source.DisplayName, material);

            if (source.Kind == GearEnhancementMaterialKind.QuartzPlate &&
                source.SourceAttributeLevel.HasValue)
            {
                AddAlias(aliases, $"quartz{source.SourceAttributeLevel.Value}", material);
            }
        }

        foreach (var source in GearMentorMaterialCatalog.AttributeDusts)
        {
            var material = ByItemId[source.ItemId];
            foreach (var alias in GearMentorMaterialCatalog.GetAliases(source))
            {
                AddAlias(aliases, alias, material);
            }
        }

        return aliases;
    }

    private static void AddAlias(
        Dictionary<string, DeveloperGrantMaterialDefinition> aliases,
        string alias,
        DeveloperGrantMaterialDefinition material)
    {
        var normalized = NormalizeAlias(alias);
        if (!aliases.TryAdd(normalized, material) && aliases[normalized].ItemId != material.ItemId)
        {
            throw new InvalidOperationException($"Developer material alias '{alias}' is ambiguous.");
        }
    }

    private static string NormalizeAlias(string alias)
    {
        return string.Concat(alias.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }
}
