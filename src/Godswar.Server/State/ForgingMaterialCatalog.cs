using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal static class ForgingMaterialCatalog
{
    public const string ConsumeItemType = "consume item";
    public const short ShippedStackCap = 99;

    public static IReadOnlyList<ForgingMaterialDefinition> All { get; } =
    [
        new(4200, "MaterialID1", "Level 1 Ruby", ConsumeItemType, ShippedStackCap, "ruby", 1, false, "./Localization/en_us/UI/Texture/Icon.gwo", "756,936"),
        new(4201, "MaterialID2", "Level 2 Ruby", ConsumeItemType, ShippedStackCap, "ruby", 2, false, "./Localization/en_us/UI/Texture/Icon.gwo", "792,936", 1),
        new(4202, "MaterialID3", "Level 3 Ruby", ConsumeItemType, ShippedStackCap, "ruby", 3, false, "./Localization/en_us/UI/Texture/Icon.gwo", "828,936", 1),

        new(4210, "MaterialBase1", "Level 1 Sapphire", ConsumeItemType, ShippedStackCap, "sapphire", 1, false, "./Localization/en_us/UI/Texture/Icon.gwo", "864,936"),
        new(4211, "MaterialBase2", "Level 2 Sapphire", ConsumeItemType, ShippedStackCap, "sapphire", 2, false, "./Localization/en_us/UI/Texture/Icon.gwo", "900,936", 1),
        new(4212, "MaterialBase3", "Level 3 Sapphire", ConsumeItemType, ShippedStackCap, "sapphire", 3, false, "./Localization/en_us/UI/Texture/Icon.gwo", "936,936", 1),
        new(4213, "MaterialBase4", "Level 4 Sapphire", ConsumeItemType, ShippedStackCap, "sapphire", 4, false, "./Localization/en_us/UI/Texture/Icon2.gwo", "540,612", 1),
        new(4214, "MaterialBase5", "Level 4 Sapphire Pieces", ConsumeItemType, ShippedStackCap, "sapphire", 4, true, "./Localization/en_us/UI/Texture/Icon2.gwo", "936,648", 1),
        new(4215, "MaterialBase6", "Level 5 Sapphire", ConsumeItemType, ShippedStackCap, "sapphire", 5, false, "./Localization/en_us/UI/Texture/Icon4.gwo", "36,0", 1),
        new(4216, "MaterialBase7", "Level 5 Sapphire Pieces", ConsumeItemType, ShippedStackCap, "sapphire", 5, true, "./Localization/en_us/UI/Texture/Icon4.gwo", "144,0", 1),

        new(4220, "MaterialAppend1", "Level 1 Emerald", ConsumeItemType, ShippedStackCap, "emerald", 1, false, "./Localization/en_us/UI/Texture/Icon.gwo", "648,936"),
        new(4221, "MaterialAppend2", "Level 2 Emerald", ConsumeItemType, ShippedStackCap, "emerald", 2, false, "./Localization/en_us/UI/Texture/Icon.gwo", "684,936", 1),
        new(4222, "MaterialAppend3", "Level 3 Emerald", ConsumeItemType, ShippedStackCap, "emerald", 3, false, "./Localization/en_us/UI/Texture/Icon.gwo", "720,936", 1),
        new(4223, "MaterialAppend4", "Level 4 Emerald", ConsumeItemType, ShippedStackCap, "emerald", 4, false, "./Localization/en_us/UI/Texture/Icon2.gwo", "576,612", 1),
        new(4224, "MaterialAppend5", "Level 4 Emerald Pieces", ConsumeItemType, ShippedStackCap, "emerald", 4, true, "./Localization/en_us/UI/Texture/Icon2.gwo", "900,648", 1),
        new(4225, "MaterialAppend6", "Level 5 Emerald", ConsumeItemType, ShippedStackCap, "emerald", 5, false, "./Localization/en_us/UI/Texture/Icon4.gwo", "72,0", 1),
        new(4226, "MaterialAppend7", "Level 5 Emerald Pieces", ConsumeItemType, ShippedStackCap, "emerald", 5, true, "./Localization/en_us/UI/Texture/Icon4.gwo", "180,0", 1),

        new(4230, "MaterialOdds1", "Level 1 Crystal", ConsumeItemType, ShippedStackCap, "crystal", 1, false, "./Localization/en_us/UI/Texture/Icon.gwo", "432,936"),
        new(4231, "MaterialOdds2", "Level 2 Crystal", ConsumeItemType, ShippedStackCap, "crystal", 2, false, "./Localization/en_us/UI/Texture/Icon.gwo", "468,936", 1, 2, "201,201"),
        new(4232, "MaterialOdds3", "Level 3 Crystal", ConsumeItemType, ShippedStackCap, "crystal", 3, false, "./Localization/en_us/UI/Texture/Icon.gwo", "504,936", 1),
        new(4233, "MaterialOdds4", "Level 4 Crystal", ConsumeItemType, ShippedStackCap, "crystal", 4, false, "./Localization/en_us/UI/Texture/Icon2.gwo", "504,612", 1),
        new(4234, "MaterialOdds5", "Level 5 Crystal", ConsumeItemType, ShippedStackCap, "crystal", 5, false, "./Localization/en_us/UI/Texture/Icon4.gwo", "0,0", 1),
        new(4235, "MaterialOdds6", "Level 5 Crystal Pieces", ConsumeItemType, ShippedStackCap, "crystal", 5, true, "./Localization/en_us/UI/Texture/Icon4.gwo", "108,0", 1)
    ];

    private static readonly IReadOnlyDictionary<uint, ForgingMaterialDefinition> ByItemId =
        All.ToDictionary(material => material.ItemId);

    private static readonly IReadOnlyDictionary<string, ForgingMaterialDefinition> ByAlias =
        CreateAliasMap();

    public static bool TryResolve(uint itemId, out ForgingMaterialDefinition material)
    {
        return ByItemId.TryGetValue(itemId, out material!);
    }

    public static bool TryResolve(string alias, out ForgingMaterialDefinition material)
    {
        return ByAlias.TryGetValue(NormalizeAlias(alias), out material!);
    }

    private static IReadOnlyDictionary<string, ForgingMaterialDefinition> CreateAliasMap()
    {
        var aliases = new Dictionary<string, ForgingMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in All)
        {
            foreach (var alias in GetAliases(material))
            {
                AddAlias(aliases, alias, material);
            }
        }

        return aliases;
    }

    internal static IEnumerable<string> GetAliases(ForgingMaterialDefinition material)
    {
        yield return material.CanonicalAlias;
        yield return material.NameKey;
        yield return material.DisplayName;

        if (material.IsPiece)
        {
            yield return $"{material.Material}level{material.Level}pieces";
            yield break;
        }

        yield return $"{material.Material}lv{material.Level}";
        yield return $"{material.Material}l{material.Level}";
        yield return $"{material.Material}level{material.Level}";
        yield return $"level{material.Level}{material.Material}";
    }

    private static void AddAlias(
        Dictionary<string, ForgingMaterialDefinition> aliases,
        string alias,
        ForgingMaterialDefinition material)
    {
        var normalized = NormalizeAlias(alias);
        if (!aliases.TryAdd(normalized, material) && aliases[normalized].ItemId != material.ItemId)
        {
            throw new InvalidOperationException($"Forging material alias '{alias}' is ambiguous.");
        }
    }

    private static string NormalizeAlias(string alias)
    {
        return string.Concat(alias.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }
}
