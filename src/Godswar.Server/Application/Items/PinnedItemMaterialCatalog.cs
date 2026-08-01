using System.Collections.Frozen;

namespace Godswar.Server.Application.Items;

internal sealed class PinnedItemMaterialCatalog : IItemMaterialCatalog
{
    private readonly FrozenDictionary<uint, ForgingMaterialDefinition> _forgingById;
    private readonly FrozenDictionary<string, ForgingMaterialDefinition> _forgingByAlias;
    private readonly FrozenDictionary<uint, GearEnhancementMaterialDefinition> _enhancementById;
    private readonly FrozenDictionary<uint, AttributeDustDefinition> _dustById;
    private readonly FrozenDictionary<uint, AttributeDustDefinition> _dustByStoneId;
    private readonly FrozenDictionary<int, AttributeDustDefinition> _dustByAttributeId;
    private readonly FrozenDictionary<uint, GearMentorMaterialRecipeDefinition>
        _crystalTransformsBySourceId;
    private readonly FrozenDictionary<uint, GearMentorMaterialRecipeDefinition>
        _gemPieceCombinationsBySourceId;
    private readonly FrozenDictionary<uint, DeveloperGrantMaterialDefinition> _developerById;
    private readonly FrozenDictionary<string, DeveloperGrantMaterialDefinition> _developerByAlias;

    private PinnedItemMaterialCatalog(
        ForgingMaterialDefinition[] forging,
        GearEnhancementMaterialDefinition[] enhancement,
        AttributeDustDefinition[] dusts,
        GearMentorMaterialRecipeDefinition[] recipes)
    {
        ForgingMaterials = Array.AsReadOnly(forging);
        GearEnhancementMaterials = Array.AsReadOnly(enhancement);
        AttributeStones = Array.AsReadOnly(enhancement
            .Where(static value => value.Kind == GearEnhancementMaterialKind.AttributeStone)
            .ToArray());
        AttributeDusts = Array.AsReadOnly(dusts);
        GearMentorRecipes = Array.AsReadOnly(recipes);
        _forgingById = forging.ToFrozenDictionary(static value => value.ItemId);
        _enhancementById = enhancement.ToFrozenDictionary(static value => value.ItemId);
        _dustById = dusts.ToFrozenDictionary(static value => value.ItemId);
        _dustByStoneId = dusts.ToFrozenDictionary(static value => value.AttributeStoneItemId);
        _dustByAttributeId = CreateDustAttributeMap(dusts, _enhancementById)
            .ToFrozenDictionary();
        _crystalTransformsBySourceId = recipes
            .Where(static value =>
                value.Kind == GearMentorMaterialRecipeKind.CrystalTransform)
            .ToFrozenDictionary(static value => value.SourceItemId);
        _gemPieceCombinationsBySourceId = recipes
            .Where(static value =>
                value.Kind == GearMentorMaterialRecipeKind.GemPieceCombination)
            .ToFrozenDictionary(static value => value.SourceItemId);
        _forgingByAlias = CreateForgingAliasMap(forging).ToFrozenDictionary(
            StringComparer.OrdinalIgnoreCase);
        var developer = CreateDeveloperMaterials(forging, enhancement, dusts);
        DeveloperMaterials = Array.AsReadOnly(developer);
        _developerById = developer.ToFrozenDictionary(static value => value.ItemId);
        _developerByAlias = CreateDeveloperAliasMap(
                forging,
                enhancement,
                dusts,
                _developerById)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ForgingMaterialDefinition> ForgingMaterials { get; }
    public IReadOnlyList<GearEnhancementMaterialDefinition> GearEnhancementMaterials { get; }
    public IReadOnlyList<GearEnhancementMaterialDefinition> AttributeStones { get; }
    public IReadOnlyList<AttributeDustDefinition> AttributeDusts { get; }
    public IReadOnlyList<GearMentorMaterialRecipeDefinition> GearMentorRecipes { get; }
    public IReadOnlyList<DeveloperGrantMaterialDefinition> DeveloperMaterials { get; }

    public bool TryResolveForging(uint itemId, out ForgingMaterialDefinition material) =>
        _forgingById.TryGetValue(itemId, out material!);

    public bool TryResolveForging(string alias, out ForgingMaterialDefinition material) =>
        _forgingByAlias.TryGetValue(NormalizeAlias(alias), out material!);

    public bool TryGetGearEnhancement(uint itemId, out GearEnhancementMaterialDefinition material) =>
        _enhancementById.TryGetValue(itemId, out material!);

    public bool TryGetAttributeStone(uint itemId, out GearEnhancementMaterialDefinition material) =>
        TryGetGearEnhancement(itemId, out material!) &&
        material.Kind == GearEnhancementMaterialKind.AttributeStone;

    public bool TryGetDust(uint itemId, out AttributeDustDefinition dust) =>
        _dustById.TryGetValue(itemId, out dust!);

    public bool TryGetDustForStone(uint stoneItemId, out AttributeDustDefinition dust) =>
        _dustByStoneId.TryGetValue(stoneItemId, out dust!);

    public bool TryGetDustForAttribute(int attributeId, out AttributeDustDefinition dust) =>
        _dustByAttributeId.TryGetValue(attributeId, out dust!);

    public bool TryResolveCrystalTransform(
        uint sourceItemId,
        out GearMentorMaterialRecipeDefinition recipe) =>
        _crystalTransformsBySourceId.TryGetValue(sourceItemId, out recipe!);

    public bool TryResolveGemPieceCombination(
        uint sourceItemId,
        out GearMentorMaterialRecipeDefinition recipe) =>
        _gemPieceCombinationsBySourceId.TryGetValue(sourceItemId, out recipe!);

    public bool TryResolveDeveloper(uint itemId, out DeveloperGrantMaterialDefinition material) =>
        _developerById.TryGetValue(itemId, out material!);

    public bool TryResolveDeveloper(string alias, out DeveloperGrantMaterialDefinition material) =>
        _developerByAlias.TryGetValue(NormalizeAlias(alias), out material!);

    public int ResolveStackCap(uint itemId)
    {
        if (TryGetDust(itemId, out var dust)) return dust.StackCap;
        if (TryGetGearEnhancement(itemId, out var enhancement)) return enhancement.StackCap;
        if (TryResolveForging(itemId, out var forging)) return forging.StackCap;
        throw new InvalidOperationException(
            $"Material item {itemId} has no pinned material definition.");
    }

    public static PinnedItemMaterialCatalog Create(
        IReadOnlyList<ForgingMaterialDefinition> forging,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancement,
        IReadOnlyList<AttributeDustDefinition> dusts,
        IReadOnlySet<uint>? knownTemplateIds = null,
        bool allowEmpty = false) =>
        CreateCore(
            forging,
            enhancement,
            dusts,
            [],
            knownTemplateIds,
            allowEmpty,
            requireRecipes: false);

    public static PinnedItemMaterialCatalog Create(
        IReadOnlyList<ForgingMaterialDefinition> forging,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancement,
        IReadOnlyList<AttributeDustDefinition> dusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> recipes,
        IReadOnlySet<uint>? knownTemplateIds = null) =>
        CreateCore(
            forging,
            enhancement,
            dusts,
            recipes,
            knownTemplateIds,
            allowEmpty: false,
            requireRecipes: true);

    private static PinnedItemMaterialCatalog CreateCore(
        IReadOnlyList<ForgingMaterialDefinition> forging,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancement,
        IReadOnlyList<AttributeDustDefinition> dusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> recipes,
        IReadOnlySet<uint>? knownTemplateIds,
        bool allowEmpty,
        bool requireRecipes)
    {
        ArgumentNullException.ThrowIfNull(forging);
        ArgumentNullException.ThrowIfNull(enhancement);
        ArgumentNullException.ThrowIfNull(dusts);
        ArgumentNullException.ThrowIfNull(recipes);
        var forgingSnapshot = forging.OrderBy(static value => value.ItemId).ToArray();
        var enhancementSnapshot = enhancement
            .Select(static value => value with
            {
                AttributeChain = value.AttributeChain is null
                    ? null
                    : Array.AsReadOnly(value.AttributeChain.ToArray())
            })
            .OrderBy(static value => value.ItemId)
            .ToArray();
        var dustSnapshot = dusts.OrderBy(static value => value.ItemId).ToArray();
        var recipeSnapshot = recipes
            .OrderBy(static value => value.Kind)
            .ThenBy(static value => value.SourceItemId)
            .ToArray();
        var allIds = forgingSnapshot.Select(static value => value.ItemId)
            .Concat(enhancementSnapshot.Select(static value => value.ItemId))
            .Concat(dustSnapshot.Select(static value => value.ItemId))
            .ToArray();
        if ((!allowEmpty && allIds.Length == 0) ||
            allIds.Any(static id => id == 0) ||
            allIds.Distinct().Count() != allIds.Length ||
            forgingSnapshot.Any(static value => !ValidCommon(value.NameKey, value.DisplayName, value.StackCap)) ||
            enhancementSnapshot.Any(static value => !ValidCommon(value.NameKey, value.DisplayName, value.StackCap)) ||
            dustSnapshot.Any(static value =>
                !ValidCommon(value.NameKey, value.DisplayName, value.StackCap) ||
                value.RecipeQuantity <= 0))
        {
            throw new InvalidOperationException("The published item-material snapshot is empty or invalid.");
        }
        ValidateRecipes(
            recipeSnapshot,
            forgingSnapshot,
            requireRecipes);
        if (knownTemplateIds is not null && allIds.Any(id => !knownTemplateIds.Contains(id)))
        {
            throw new InvalidOperationException("A published item-material policy references a missing item template.");
        }
        if (knownTemplateIds is not null && recipeSnapshot.Any(value =>
                !knownTemplateIds.Contains(value.SourceItemId) ||
                !knownTemplateIds.Contains(value.TargetItemId)))
        {
            throw new InvalidOperationException(
                "A published Gear Mentor material recipe references a missing item template.");
        }
        return new PinnedItemMaterialCatalog(
            forgingSnapshot,
            enhancementSnapshot,
            dustSnapshot,
            recipeSnapshot);
    }

    private static void ValidateRecipes(
        GearMentorMaterialRecipeDefinition[] recipes,
        IReadOnlyList<ForgingMaterialDefinition> forgingMaterials,
        bool requireRecipes)
    {
        if (requireRecipes &&
            Enum.GetValues<GearMentorMaterialRecipeKind>()
                .Any(kind => recipes.All(value => value.Kind != kind)))
        {
            throw new InvalidOperationException(
                "The published Gear Mentor material recipes are incomplete.");
        }
        if (recipes.Select(static value => value.SourceItemId)
                .Distinct().Count() != recipes.Length ||
            recipes.Any(static value =>
                value.Kind is not (
                    GearMentorMaterialRecipeKind.CrystalTransform or
                    GearMentorMaterialRecipeKind.GemPieceCombination) ||
                value.SourceItemId == 0 ||
                value.TargetItemId == 0 ||
                value.SourceItemId == value.TargetItemId ||
                value.SourceQuantity <= 0 ||
                value.TargetQuantity <= 0))
        {
            throw new InvalidOperationException(
                "The published Gear Mentor material recipes are invalid or ambiguous.");
        }

        var forgingById = forgingMaterials.ToDictionary(
            static value => value.ItemId);
        if (recipes.Any(value =>
                !forgingById.TryGetValue(value.SourceItemId, out var source) ||
                !forgingById.TryGetValue(value.TargetItemId, out var target) ||
                target.IsPiece ||
                (value.Kind == GearMentorMaterialRecipeKind.CrystalTransform &&
                 source.IsPiece) ||
                (value.Kind == GearMentorMaterialRecipeKind.GemPieceCombination &&
                 !source.IsPiece)))
        {
            throw new InvalidOperationException(
                "A published Gear Mentor material recipe references a missing forging-material policy.");
        }
    }

    private static bool ValidCommon(string nameKey, string displayName, short stackCap) =>
        !string.IsNullOrWhiteSpace(nameKey) &&
        !string.IsNullOrWhiteSpace(displayName) &&
        stackCap > 0;

    private static Dictionary<int, AttributeDustDefinition> CreateDustAttributeMap(
        IEnumerable<AttributeDustDefinition> dusts,
        IReadOnlyDictionary<uint, GearEnhancementMaterialDefinition> enhancement)
    {
        var map = new Dictionary<int, AttributeDustDefinition>();
        foreach (var dust in dusts)
        {
            if (!enhancement.TryGetValue(dust.AttributeStoneItemId, out var stone) ||
                stone.Kind != GearEnhancementMaterialKind.AttributeStone)
            {
                throw new InvalidOperationException(
                    $"Dust {dust.ItemId} references missing Attribute Stone {dust.AttributeStoneItemId}.");
            }
            foreach (var attributeId in stone.AllowedAttributeIds) map.TryAdd(attributeId, dust);
        }
        return map;
    }

    private static Dictionary<string, ForgingMaterialDefinition> CreateForgingAliasMap(
        IEnumerable<ForgingMaterialDefinition> materials)
    {
        var aliases = new Dictionary<string, ForgingMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials)
            foreach (var alias in ForgingAliases(material)) AddAlias(aliases, alias, material, static value => value.ItemId);
        return aliases;
    }

    private static DeveloperGrantMaterialDefinition[] CreateDeveloperMaterials(
        IEnumerable<ForgingMaterialDefinition> forging,
        IEnumerable<GearEnhancementMaterialDefinition> enhancement,
        IEnumerable<AttributeDustDefinition> dusts) =>
        forging.Select(static value => new DeveloperGrantMaterialDefinition(
                value.ItemId, value.DisplayName, value.StackCap, value.GrantedBound))
            .Concat(enhancement.Select(static value => new DeveloperGrantMaterialDefinition(
                value.ItemId, value.DisplayName, value.StackCap, 0)))
            .Concat(dusts.Select(static value => new DeveloperGrantMaterialDefinition(
                value.ItemId, value.DisplayName, value.StackCap, value.GrantedBound)))
            .OrderBy(static value => value.ItemId)
            .ToArray();

    private static Dictionary<string, DeveloperGrantMaterialDefinition> CreateDeveloperAliasMap(
        IEnumerable<ForgingMaterialDefinition> forging,
        IEnumerable<GearEnhancementMaterialDefinition> enhancement,
        IEnumerable<AttributeDustDefinition> dusts,
        IReadOnlyDictionary<uint, DeveloperGrantMaterialDefinition> byId)
    {
        var aliases = new Dictionary<string, DeveloperGrantMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in forging)
            foreach (var alias in ForgingAliases(source)) AddAlias(aliases, alias, byId[source.ItemId], static value => value.ItemId);
        foreach (var source in enhancement)
        {
            AddAlias(aliases, source.NameKey, byId[source.ItemId], static value => value.ItemId);
            AddAlias(aliases, source.DisplayName, byId[source.ItemId], static value => value.ItemId);
            if (source.Kind == GearEnhancementMaterialKind.QuartzPlate && source.SourceAttributeLevel.HasValue)
                AddAlias(aliases, $"quartz{source.SourceAttributeLevel.Value}", byId[source.ItemId], static value => value.ItemId);
        }
        foreach (var source in dusts)
            foreach (var alias in DustAliases(source)) AddAlias(aliases, alias, byId[source.ItemId], static value => value.ItemId);
        return aliases;
    }

    private static IEnumerable<string> ForgingAliases(ForgingMaterialDefinition material)
    {
        yield return material.CanonicalAlias;
        yield return material.NameKey;
        yield return material.DisplayName;
        if (material.IsPiece) { yield return $"{material.Material}level{material.Level}pieces"; yield break; }
        yield return $"{material.Material}lv{material.Level}";
        yield return $"{material.Material}l{material.Level}";
        yield return $"{material.Material}level{material.Level}";
        yield return $"level{material.Level}{material.Material}";
    }

    private static IEnumerable<string> DustAliases(AttributeDustDefinition dust)
    {
        yield return dust.NameKey;
        yield return dust.DisplayName;
        yield return dust.DisplayName.Replace(" Dust", "Dust", StringComparison.Ordinal);
    }

    private static void AddAlias<T>(Dictionary<string, T> aliases, string alias, T value, Func<T, uint> id)
    {
        var normalized = NormalizeAlias(alias);
        if (!aliases.TryAdd(normalized, value) && id(aliases[normalized]) != id(value))
            throw new InvalidOperationException($"Material alias '{alias}' is ambiguous.");
    }

    private static string NormalizeAlias(string alias) =>
        string.Concat((alias ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
