using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// Selects the single world boss that can award faction control for each
/// eligible outdoor area. A captured monster template having rank "boss" is
/// not sufficient by itself: elites and secondary bosses remain ordinary
/// encounters unless they are explicitly selected here.
/// </summary>
internal sealed class WorldBossCatalog
{
    internal static readonly TimeSpan DefaultRespawnInterval = TimeSpan.FromHours(12);

    private readonly IReadOnlyDictionary<short, WorldBossDefinition> _byMap;
    private readonly IReadOnlySet<short> _eligibleMaps;

    private WorldBossCatalog(
        IReadOnlyList<WorldBossDefinition> definitions,
        IReadOnlyList<PendingWorldBossArea> pendingAreas,
        TimeSpan respawnInterval,
        bool validateCapturedTemplates)
    {
        if (definitions.Count == 0)
        {
            throw new InvalidDataException("At least one world-boss area must be configured.");
        }

        if (respawnInterval <= TimeSpan.Zero)
        {
            throw new InvalidDataException("The world-boss respawn interval must be positive.");
        }

        var byMap = new Dictionary<short, WorldBossDefinition>();
        var eligibleMaps = new HashSet<short>();
        foreach (var definition in definitions)
        {
            ValidateDefinition(definition);
            if (!byMap.TryAdd(definition.MapId, definition))
            {
                throw new InvalidDataException(
                    $"Map {definition.MapId} has more than one selected world boss.");
            }

            eligibleMaps.Add(definition.MapId);
        }

        foreach (var pendingArea in pendingAreas)
        {
            if (pendingArea.MapId < 0 ||
                string.IsNullOrWhiteSpace(pendingArea.SceneKey) ||
                string.IsNullOrWhiteSpace(pendingArea.Reason))
            {
                throw new InvalidDataException(
                    "Pending world-boss areas require a map, scene, and reason.");
            }

            if (!eligibleMaps.Add(pendingArea.MapId))
            {
                throw new InvalidDataException(
                    $"Map {pendingArea.MapId} is duplicated in the world-boss area catalog.");
            }
        }

        Definitions = definitions;
        PendingAreas = pendingAreas;
        RespawnInterval = respawnInterval;
        _byMap = byMap;
        _eligibleMaps = eligibleMaps;

        if (validateCapturedTemplates)
        {
            ValidateCapturedTemplates();
        }
    }

    public static WorldBossCatalog Default { get; } = new(
        CreateDefaultDefinitions(),
        CreateDefaultPendingAreas(),
        DefaultRespawnInterval,
        validateCapturedTemplates: true);

    public IReadOnlyList<WorldBossDefinition> Definitions { get; }

    /// <summary>
    /// Outdoor areas that satisfy the eligibility rule but cannot be enabled
    /// until a distinct neutral world-boss template and spawn are authored.
    /// Existing faction objectives and elites must not be reused.
    /// </summary>
    public IReadOnlyList<PendingWorldBossArea> PendingAreas { get; }

    /// <summary>
    /// Two refreshes per day. The eventual scheduler may anchor those two
    /// windows to server time without changing the area/boss catalog.
    /// </summary>
    public TimeSpan RespawnInterval { get; }

    public bool IsEligibleArea(short mapId) => _eligibleMaps.Contains(mapId);

    public bool TryGet(short mapId, out WorldBossDefinition definition) =>
        _byMap.TryGetValue(mapId, out definition!);

    public bool IsWorldBoss(short mapId, string templateKey) =>
        _byMap.TryGetValue(mapId, out var definition) &&
        string.Equals(definition.TemplateKey, templateKey, StringComparison.Ordinal);

    internal static WorldBossCatalog Create(
        IEnumerable<WorldBossDefinition> definitions,
        TimeSpan respawnInterval)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return new WorldBossCatalog(
            definitions.ToArray(),
            [],
            respawnInterval,
            validateCapturedTemplates: false);
    }

    private static IReadOnlyList<WorldBossDefinition> CreateDefaultDefinitions() =>
    [
        new(3, "Parnitha_1", "A_boss_boar_001", "Boar King Tomas"),
        new(5, "Nemea_1", "A_boss_wolf_005", "Astrien"),
        new(6, "Mycenae_All", "A_boss_kingofscorpion_001", "[BOSS]Darkmist"),
        new(7, "Olympia_All", "C_boss_centaur_001", "Centaur Leader"),
        new(8, "Thermopylae_All", "B_bossB_xerxes_001", "Mardonius"),
        new(9, "Thebes_All", "A_boss_kingofscorpiondi_001", "[BOSS]Scorpion Lord Selket"),
        new(10, "Larissa_All", "C_boss_dragon_014", "Little Demate"),
        new(11, "Marathon_All", "A_boss_bull_001", "Minos the Bull King"),
        new(12, "Parnitha_2", "B_bossB_octopus_001", "Naga Siren Eirsigel"),
        new(13, "Peloponnese_All", "A_boss_spider_008", "Spider Queen Ala"),
        new(14, "Nemea_2", "B_bossB_spriggan_001", "Evil Treant Falio"),
        new(15, "Derveni_All", "B_boss_centaur_001", "Centaur Shaikh Hailer"),
        new(16, "Argolis_All", "A_boss_amazon_004", "Leader Cassirer"),
        new(17, "Isthmus_of_Corinth_All", "B_boss_dragon_001", "Red Dragon Puluo"),
        new(18, "Megara_All", "A_boss_mage_018", "Lord Barryonyx"),
        new(19, "Plataea_All", "B_boss_cyclops_001", "Giant Alcyoneus"),
        new(20, "Oracle_of_Delphi_All", "A_boss_long_005", "Hydra Lord Xausa"),
        new(21, "Olympus_All", "C_boss_dragon_013", "Bahamut"),
        new(22, "Elasson_All", "C_boss_dragon_002", "Ice Dragon")
    ];

    private static IReadOnlyList<PendingWorldBossArea> CreateDefaultPendingAreas() =>
    [
        new(
            68,
            "Parnassus",
            "Outdoor faction area; requires a new neutral boss because its Athenian and Spartan Generals are opposing-faction quest objectives.")
    ];

    private static void ValidateDefinition(WorldBossDefinition definition)
    {
        if (definition.MapId < 0 ||
            string.IsNullOrWhiteSpace(definition.SceneKey) ||
            string.IsNullOrWhiteSpace(definition.TemplateKey) ||
            string.IsNullOrWhiteSpace(definition.DisplayName))
        {
            throw new InvalidDataException("World-boss definitions require a map, scene, template, and name.");
        }

        if (HasEliteLabel(definition.DisplayName))
        {
            throw new InvalidDataException(
                $"Elite monster '{definition.DisplayName}' cannot be selected as a world boss.");
        }
    }

    private void ValidateCapturedTemplates()
    {
        foreach (var definition in Definitions)
        {
            var matches = MonsterTemplateSeeds.Monsters
                .Where(template =>
                    template.SourceMapId == definition.MapId &&
                    string.Equals(template.TemplateKey, definition.TemplateKey, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    $"World boss '{definition.DisplayName}' does not resolve to one captured map template.");
            }

            var template = matches[0];
            if (!template.IsBoss ||
                template.IsElite ||
                template.IsPet ||
                !string.Equals(template.Rank, "boss", StringComparison.OrdinalIgnoreCase) ||
                HasEliteLabel(template.DisplayName))
            {
                throw new InvalidDataException(
                    $"Template '{definition.TemplateKey}' is not a valid non-elite world-boss source.");
            }
        }
    }

    private static bool HasEliteLabel(string name) =>
        name.StartsWith("[Elite]", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("[E]", StringComparison.OrdinalIgnoreCase);
}

internal sealed record WorldBossDefinition(
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName);

internal sealed record PendingWorldBossArea(
    short MapId,
    string SceneKey,
    string Reason);
