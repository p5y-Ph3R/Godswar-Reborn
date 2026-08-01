using Godswar.Server.Application.World;

namespace Godswar.Server.Game;

/// <summary>
/// Selects the single world boss that can award faction control for each
/// eligible outdoor area. A published monster template having rank "boss" is
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
        IReadOnlyList<GameplayMonsterTemplateDefinition>? monsterTemplates,
        bool allowEmpty = false)
    {
        if (definitions.Count == 0 && !allowEmpty)
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

        if (definitions.Count > 0 && monsterTemplates is not null)
        {
            ValidatePublishedTemplates(monsterTemplates);
        }
    }

    public static WorldBossCatalog Empty { get; } = new(
        [],
        [],
        DefaultRespawnInterval,
        [],
        allowEmpty: true);

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

    public TimeSpan ResolveRespawnInterval(
        short mapId,
        string templateKey,
        TimeSpan ordinaryRespawnInterval) =>
        _byMap.TryGetValue(mapId, out var definition) &&
        string.Equals(
            definition.TemplateKey,
            templateKey,
            StringComparison.Ordinal)
            ? definition.RespawnInterval
            : ordinaryRespawnInterval;

    public static WorldBossCatalog Create(
        GameplayContentCatalog gameplay)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        if (gameplay.WorldBosses.Count == 0 &&
            gameplay.PendingWorldBossAreas.Count == 0)
        {
            return Empty;
        }

        var definitions = gameplay.WorldBosses
            .Select(static boss => new WorldBossDefinition(
                boss.MapId,
                boss.SceneKey,
                boss.TemplateKey,
                boss.DisplayName,
                boss.BonusBasisPoints,
                boss.RespawnInterval))
            .ToArray();
        var pendingAreas = gameplay.PendingWorldBossAreas
            .Select(static area => new PendingWorldBossArea(
                area.MapId,
                area.SceneKey,
                area.Reason))
            .ToArray();
        var defaultRespawnInterval = definitions.Length == 0
            ? DefaultRespawnInterval
            : definitions[0].RespawnInterval;
        return new WorldBossCatalog(
            definitions,
            pendingAreas,
            defaultRespawnInterval,
            gameplay.MonsterTemplates,
            allowEmpty: definitions.Length == 0);
    }

    internal static WorldBossCatalog Create(
        IEnumerable<WorldBossDefinition> definitions,
        TimeSpan respawnInterval)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var normalized = definitions
            .Select(definition => definition.RespawnInterval > TimeSpan.Zero
                ? definition
                : definition with
                {
                    RespawnInterval = respawnInterval
                })
            .ToArray();
        return new WorldBossCatalog(
            normalized,
            [],
            respawnInterval,
            monsterTemplates: null);
    }

    private static void ValidateDefinition(WorldBossDefinition definition)
    {
        if (definition.MapId < 0 ||
            string.IsNullOrWhiteSpace(definition.SceneKey) ||
            string.IsNullOrWhiteSpace(definition.TemplateKey) ||
            string.IsNullOrWhiteSpace(definition.DisplayName) ||
            definition.BonusBasisPoints < 0 ||
            definition.RespawnInterval <= TimeSpan.Zero)
        {
            throw new InvalidDataException("World-boss definitions require a map, scene, template, and name.");
        }

        if (HasEliteLabel(definition.DisplayName))
        {
            throw new InvalidDataException(
                $"Elite monster '{definition.DisplayName}' cannot be selected as a world boss.");
        }
    }

    private void ValidatePublishedTemplates(
        IReadOnlyList<GameplayMonsterTemplateDefinition> monsterTemplates)
    {
        foreach (var definition in Definitions)
        {
            var matches = monsterTemplates
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
    string DisplayName,
    int BonusBasisPoints = 0,
    TimeSpan RespawnInterval = default);

internal sealed record PendingWorldBossArea(
    short MapId,
    string SceneKey,
    string Reason);
