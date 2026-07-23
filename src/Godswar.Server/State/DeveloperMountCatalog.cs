using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Godswar.Server.State;

internal sealed record DeveloperMountDefinition(
    uint ItemId,
    string NameKey,
    string DisplayName,
    string FamilyAlias,
    string Tier,
    int RequiredLevel,
    float SpeedBonus,
    int? DurationDays,
    bool IsLegacy,
    bool CanGrant);

internal sealed record DeveloperMountFamilyDefinition(
    string Alias,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<DeveloperMountDefinition> Mounts);

/// <summary>
/// Closed developer-command catalog reconstructed from the client's mount
/// templates. Numeric IDs remain canonical because the client deliberately
/// reuses display names and even NameKey values for several distinct mounts.
/// </summary>
internal static class DeveloperMountCatalog
{
    public const int FamiliesPerPage = 4;
    public const uint OrphanedMountItemId = 14429;

    private static readonly FrozenDictionary<uint, ItemTemplateSeed> ClientMountTemplates =
        ItemTemplateSeeds.All
            .Where(static template =>
                template.Kind.Equals("mount", StringComparison.OrdinalIgnoreCase))
            .ToFrozenDictionary(static template => checked((uint)template.Id));

    public static IReadOnlyList<DeveloperMountFamilyDefinition> Families { get; } =
        CreateFamilies();

    public static IReadOnlyList<DeveloperMountDefinition> All { get; } =
        Families.SelectMany(static family => family.Mounts).ToArray();

    public static IReadOnlyList<DeveloperMountDefinition> Grantable { get; } =
        All.Where(static mount => mount.CanGrant).ToArray();

    public static int PageCount =>
        (Families.Count + FamiliesPerPage - 1) / FamiliesPerPage;

    private static readonly FrozenDictionary<uint, DeveloperMountDefinition> ByItemId =
        All.ToFrozenDictionary(static mount => mount.ItemId);

    private static readonly FrozenDictionary<uint, DeveloperMountDefinition> GrantableByItemId =
        Grantable.ToFrozenDictionary(static mount => mount.ItemId);

    private static readonly FrozenDictionary<string, DeveloperMountFamilyDefinition> ByFamilyAlias =
        CreateFamilyAliasMap();

    public static bool TryGet(uint itemId, out DeveloperMountDefinition mount) =>
        ByItemId.TryGetValue(itemId, out mount!);

    public static bool TryResolveGrantable(uint itemId, out DeveloperMountDefinition mount) =>
        GrantableByItemId.TryGetValue(itemId, out mount!);

    public static bool TryGetFamily(
        string alias,
        out DeveloperMountFamilyDefinition family) =>
        ByFamilyAlias.TryGetValue(Normalize(alias), out family!);

    public static bool TryResolveGrantable(
        string familyAlias,
        string tier,
        out DeveloperMountDefinition mount)
    {
        mount = null!;
        if (!TryGetFamily(familyAlias, out var family))
        {
            return false;
        }

        var normalizedTier = NormalizeTier(tier);
        var candidate = family.Mounts.FirstOrDefault(entry =>
            NormalizeTier(entry.Tier).Equals(normalizedTier, StringComparison.OrdinalIgnoreCase));
        if (candidate is null || !candidate.CanGrant)
        {
            return false;
        }

        mount = candidate;
        return true;
    }

    public static IReadOnlyList<DeveloperMountFamilyDefinition> GetPage(int page)
    {
        if (page is < 1 || page > PageCount)
        {
            return [];
        }

        return Families
            .Skip((page - 1) * FamiliesPerPage)
            .Take(FamiliesPerPage)
            .ToArray();
    }

    private static IReadOnlyList<DeveloperMountFamilyDefinition> CreateFamilies()
    {
        var families = new List<DeveloperMountFamilyDefinition>();
        var assignedIds = new HashSet<uint>();

        AddTieredFamily(families, assignedIds, 14220, "greeksteed", "Greek Steed", ["horse", "steed"]);
        AddTieredFamily(families, assignedIds, 14240, "parnithaboar", "Parnitha Boar", ["boar"]);
        AddTieredFamily(families, assignedIds, 14260, "nemeanwolf", "Nemean Wolf", ["wolf"]);
        AddTieredFamily(families, assignedIds, 14280, "africanlion", "African Lion", ["lion"]);
        AddTieredFamily(families, assignedIds, 14300, "reindeer", "Reindeer", []);
        AddTieredFamily(families, assignedIds, 14320, "argentdragon-a", "Argent Armored Dragon A", ["argentdragona"]);
        AddTieredFamily(families, assignedIds, 14340, "flyingcarpet", "Flying Carpet", ["carpet"]);
        AddTieredFamily(families, assignedIds, 14360, "gw176motorcycle", "GW-176 Motorcycle", ["gw176", "motorcycle"]);
        AddTieredFamily(families, assignedIds, 14380, "plumpbirdie", "Plump Birdie", ["birdie"]);
        AddTieredFamily(families, assignedIds, 14400, "argentdragon-b", "Argent Armored Dragon B", ["argentdragonb"]);
        AddTieredFamily(families, assignedIds, 14440, "atlanticleatherback", "Atlantic Leatherback", ["leatherback", "turtle"]);
        AddTieredFamily(families, assignedIds, 14460, "asianurus", "Asian Urus", ["urus"]);
        AddTieredFamily(families, assignedIds, 14480, "blackbear", "Black Bear", []);
        AddTieredFamily(families, assignedIds, 14490, "yellowgocart", "Yellow Go-Cart", ["gocart"]);
        AddTieredFamily(families, assignedIds, 14510, "kitsune", "Kitsune", []);
        AddTieredFamily(families, assignedIds, 14520, "butterfly", "Butterfly", []);
        AddTieredFamily(families, assignedIds, 16000, "unicorn", "Unicorn", []);
        AddTieredFamily(families, assignedIds, 16020, "magicbroom", "Magic Broom", ["broom"]);
        AddTieredFamily(families, assignedIds, 16040, "asianelephant", "Asian Elephant", ["elephant"]);
        AddTieredFamily(families, assignedIds, 16060, "cunningcougar", "Cunning Cougar", ["cougar"]);
        AddTieredFamily(families, assignedIds, 16080, "stormdragon", "Storm Dragon", []);
        AddTieredFamily(families, assignedIds, 16100, "kharickylin", "Kharic Kylin", ["kylin"]);
        AddTieredFamily(families, assignedIds, 16120, "phoenix", "Phoenix", []);
        AddTieredFamily(families, assignedIds, 16130, "sakurabunny", "Sakura Bunny", ["bunny"]);
        AddTieredFamily(families, assignedIds, 16140, "littlellama", "Little Llama", ["llama"]);
        AddTieredFamily(families, assignedIds, 16150, "sabertooth", "Sabertooth", []);
        AddTieredFamily(families, assignedIds, 16160, "meowling", "Meowling", []);
        AddTieredFamily(families, assignedIds, 16170, "scorpionking", "Scorpion King", ["scorpion"]);
        AddTieredFamily(families, assignedIds, 16180, "panda", "Panda", []);
        AddTieredFamily(families, assignedIds, 16190, "owl", "Owl", []);
        AddTieredFamily(
            families,
            assignedIds,
            16200,
            "erebuslion",
            "Erebus Lion",
            ["blacklion", "shadowlion"]);

        AddFamily(
            families,
            assignedIds,
            "legacygreeksteed",
            "Legacy Greek Steed",
            ["oldgreeksteed"],
            [6000, 6001, 6002, 6003, 6004, 6005],
            ["base", "1", "2", "3", "4", "5"],
            isLegacy: true);
        AddFamily(
            families,
            assignedIds,
            "legacyparnithaboar",
            "Legacy Parnitha Boar",
            ["oldparnithaboar"],
            [6010, 6011, 6012, 6013, 6014, 6015],
            ["base", "1", "2", "3", "4", "5"],
            isLegacy: true);
        AddFamily(
            families,
            assignedIds,
            "legacynemeanwolf",
            "Legacy Nemean Wolf",
            ["oldnemeanwolf"],
            [6020, 6021, 6022, 6023, 6024, 6025],
            ["base", "1", "2", "3", "4", "5"],
            isLegacy: true);
        AddFamily(
            families,
            assignedIds,
            "legacyxmasreindeer",
            "Legacy Xmas Reindeer",
            ["oldxmasreindeer"],
            [6030, 6031, 6032, 6033, 6034, 6035],
            ["base", "1", "2", "3", "4", "5"],
            isLegacy: true);
        AddFamily(
            families,
            assignedIds,
            "legacyafricanlion",
            "Legacy African Lion",
            ["oldafricanlion"],
            [6041, 6042, 6043, 6044, 6045, 6046],
            ["base", "1", "2", "3", "4", "5"],
            isLegacy: true);
        AddFamily(
            families,
            assignedIds,
            "legacynemeanwolf7d",
            "Legacy Nemean Wolf (7 Days)",
            [],
            [6026],
            ["7d"],
            isLegacy: true);
        AddFamily(
            families,
            assignedIds,
            "legacyafricanlion7d",
            "Legacy African Lion (7 Days)",
            [],
            [6040],
            ["7d"],
            isLegacy: true);

        AddFamily(families, assignedIds, "timedafricanlion", "Timed African Lion", [], [14420], ["30d"]);
        AddFamily(families, assignedIds, "timedreindeer", "Timed Reindeer", [], [14421, 14426], ["30d", "7d"]);
        AddFamily(families, assignedIds, "timedgw176", "Timed GW-176 Motorcycle", ["timedmotorcycle"], [14422], ["30d"]);
        AddFamily(families, assignedIds, "timedplumpbirdie", "Timed Plump Birdie", [], [14423], ["30d"]);
        AddFamily(families, assignedIds, "timedflyingcarpet", "Timed Flying Carpet", [], [14424], ["30d"]);
        AddFamily(families, assignedIds, "timedatlanticleatherback", "Timed Atlantic Leatherback", ["timedturtle"], [14425], ["3d"]);
        AddFamily(families, assignedIds, "orphanride14428", "Orphaned Ride14428", [], [14429], ["7d"]);

        foreach (var unassigned in ClientMountTemplates.Values
                     .Where(template => !assignedIds.Contains(checked((uint)template.Id)))
                     .OrderBy(static template => template.Id))
        {
            var itemId = checked((uint)unassigned.Id);
            AddFamily(
                families,
                assignedIds,
                $"mount{itemId}",
                unassigned.DisplayName,
                [],
                [itemId],
                [(unassigned.MinLevel ?? 1).ToString(CultureInfo.InvariantCulture)]);
        }

        if (assignedIds.Count != ClientMountTemplates.Count)
        {
            throw new InvalidOperationException(
                $"Developer mount catalog assigned {assignedIds.Count} of {ClientMountTemplates.Count} client templates.");
        }

        return families;
    }

    private static void AddTieredFamily(
        List<DeveloperMountFamilyDefinition> families,
        HashSet<uint> assignedIds,
        uint baseItemId,
        string alias,
        string displayName,
        IReadOnlyList<string> aliases)
    {
        AddFamily(
            families,
            assignedIds,
            alias,
            displayName,
            aliases,
            Enumerable.Range(0, 10).Select(offset => baseItemId + (uint)offset).ToArray(),
            ["40", "50", "60", "70", "80", "90", "100", "110", "max", "special"]);
    }

    private static void AddFamily(
        List<DeveloperMountFamilyDefinition> families,
        HashSet<uint> assignedIds,
        string alias,
        string displayName,
        IReadOnlyList<string> aliases,
        IReadOnlyList<uint> itemIds,
        IReadOnlyList<string> tiers,
        bool isLegacy = false)
    {
        if (itemIds.Count != tiers.Count)
        {
            throw new InvalidOperationException($"Mount family '{alias}' has mismatched IDs and tiers.");
        }

        var mounts = new DeveloperMountDefinition[itemIds.Count];
        for (var index = 0; index < itemIds.Count; index++)
        {
            var itemId = itemIds[index];
            if (!ClientMountTemplates.TryGetValue(itemId, out var template))
            {
                throw new InvalidOperationException(
                    $"Mount family '{alias}' references missing client item {itemId}.");
            }

            if (!assignedIds.Add(itemId))
            {
                throw new InvalidOperationException(
                    $"Client mount item {itemId} was assigned to more than one family.");
            }

            mounts[index] = new DeveloperMountDefinition(
                itemId,
                template.NameKey,
                template.DisplayName,
                alias,
                tiers[index],
                template.MinLevel ?? 1,
                ReadFirstFloat(template.StatsJson, "Speed"),
                ReadDurationDays(template.StatsJson),
                isLegacy,
                CanGrant: itemId != OrphanedMountItemId);
        }

        families.Add(new DeveloperMountFamilyDefinition(
            alias,
            displayName,
            aliases,
            mounts));
    }

    private static FrozenDictionary<string, DeveloperMountFamilyDefinition> CreateFamilyAliasMap()
    {
        var aliases = new Dictionary<string, DeveloperMountFamilyDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var family in Families)
        {
            AddFamilyAlias(aliases, family.Alias, family);
            foreach (var alias in family.Aliases)
            {
                AddFamilyAlias(aliases, alias, family);
            }
        }

        return aliases.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddFamilyAlias(
        Dictionary<string, DeveloperMountFamilyDefinition> aliases,
        string alias,
        DeveloperMountFamilyDefinition family)
    {
        var normalized = Normalize(alias);
        if (!aliases.TryAdd(normalized, family) && aliases[normalized] != family)
        {
            throw new InvalidOperationException($"Developer mount family alias '{alias}' is ambiguous.");
        }
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string NormalizeTier(string value)
    {
        var normalized = Normalize(value);
        if (normalized.Length > 1 &&
            normalized[0] == 'l' &&
            normalized[1..].All(char.IsDigit))
        {
            return normalized[1..];
        }

        return normalized switch
        {
            "120" => "max",
            "maximum" => "max",
            "fast" => "special",
            _ => normalized
        };
    }

    private static float ReadFirstFloat(string statsJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            if (document.RootElement.TryGetProperty(propertyName, out var property) &&
                float.TryParse(
                    property.GetString()?.Split(',', 2)[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) &&
                float.IsFinite(value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        return 0f;
    }

    private static int? ReadDurationDays(string statsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            if (!document.RootElement.TryGetProperty("ExpiredTime", out var property))
            {
                return null;
            }

            var parts = property.GetString()?.Split(',');
            return parts is { Length: >= 3 } &&
                   int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) &&
                   days > 0
                ? days
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
