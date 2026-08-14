namespace Godswar.Server.Application.World;

internal sealed partial class PinnedWorldContentReader
{
    private const int MaximumGameplayMaps = 1_024;
    private const int MaximumGameplayAddressPoints = 100_000;
    private const int MaximumGameplayLinks = 10_000;
    private const int MaximumGameplayMonsterTemplates = 100_000;
    private const int MaximumGameplayWorldBosses = 1_024;
    private const int MaximumGameplaySkills = 100_000;
    private const int MaximumGameplayTextLength = 512;

    private static GameplayContentCatalog PinGameplay(
        GameplayContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var maps = MaterializeGameplay(
                content.Maps,
                MaximumGameplayMaps,
                "map")
            .OrderBy(static value => value.MapId)
            .ToArray();
        var mapIds = new HashSet<short>();
        foreach (var map in maps)
        {
            if (map.MapId < 0 ||
                !IsGameplayText(map.SceneKey, 96) ||
                !IsGameplayText(map.DisplayName, 128) ||
                (map.MapMode.HasValue && map.MapMode.Value is < 0 or > 5) ||
                !mapIds.Add(map.MapId))
            {
                throw Invalid(
                    "gameplay",
                    $"Gameplay map {map.MapId} is malformed or duplicated.");
            }
        }

        var addressPoints = MaterializeGameplay(
                content.AddressPoints,
                MaximumGameplayAddressPoints,
                "address point")
            .OrderBy(static value => value.MapId)
            .ThenBy(static value => value.GroupIndex)
            .ThenBy(static value => value.PointIndex)
            .ToArray();
        var addressKeys = new HashSet<(short, short, short)>();
        foreach (var point in addressPoints)
        {
            if (!mapIds.Contains(point.MapId) ||
                point.GroupIndex < 0 ||
                point.PointIndex < 0 ||
                !IsOptionalGameplayText(point.GroupName, 128) ||
                !IsOptionalGameplayText(point.Name, 128) ||
                !IsGameplayText(point.Source, 255) ||
                !float.IsFinite(point.X) ||
                !float.IsFinite(point.Z) ||
                !addressKeys.Add((
                    point.MapId,
                    point.GroupIndex,
                    point.PointIndex)))
            {
                throw Invalid("gameplay", "A gameplay address point is invalid.");
            }
        }

        var links = MaterializeGameplay(
                content.Links,
                MaximumGameplayLinks,
                "map link")
            .OrderBy(static value => value.SourceMapId)
            .ThenBy(static value => value.LinkIndex)
            .ThenBy(static value => value.TargetMapId)
            .ToArray();
        var linkKeys = new HashSet<(short, short, short)>();
        foreach (var link in links)
        {
            if (!mapIds.Contains(link.SourceMapId) ||
                !mapIds.Contains(link.TargetMapId) ||
                link.SourceMapId == link.TargetMapId ||
                link.LinkIndex < 0 ||
                !float.IsFinite(link.X) ||
                !float.IsFinite(link.Z) ||
                !IsGameplayText(link.Source, 255) ||
                !IsGameplayText(link.Note, MaximumGameplayTextLength) ||
                !Enum.IsDefined(link.Confidence) ||
                !Enum.IsDefined(link.Activation) ||
                !linkKeys.Add((
                    link.SourceMapId,
                    link.LinkIndex,
                    link.TargetMapId)))
            {
                throw Invalid("gameplay", "A gameplay map link is invalid.");
            }
        }

        var monsters = MaterializeGameplay(
                content.MonsterTemplates,
                MaximumGameplayMonsterTemplates,
                "monster template")
            .OrderBy(static value => value.SourceKey, StringComparer.Ordinal)
            .ThenBy(static value => value.TemplateKey, StringComparer.Ordinal)
            .ToArray();
        var monsterKeys = new HashSet<(string, string)>();
        foreach (var monster in monsters)
        {
            if (!IsGameplayText(monster.SourceKey, 32) ||
                !IsGameplayText(monster.SourceKind, 16) ||
                (monster.SourceMapId.HasValue &&
                 !mapIds.Contains(monster.SourceMapId.Value)) ||
                !IsOptionalGameplayText(monster.SceneKey, 96) ||
                !IsGameplayText(monster.TemplateKey, 128) ||
                !IsOptionalGameplayText(monster.DisplayName, 255) ||
                !IsGameplayText(monster.Rank, 16) ||
                (monster.AttackType.HasValue &&
                 monster.AttackType.Value is not (1 or 2 or 3)) ||
                (monster.CollisionRange.HasValue &&
                 (!float.IsFinite(monster.CollisionRange.Value) ||
                  monster.CollisionRange.Value < 0f)) ||
                !monsterKeys.Add((monster.SourceKey, monster.TemplateKey)))
            {
                throw Invalid("gameplay", "A gameplay monster template is invalid.");
            }
        }

        var bosses = MaterializeGameplay(
                content.WorldBosses,
                MaximumGameplayWorldBosses,
                "world boss")
            .OrderBy(static value => value.MapId)
            .ToArray();
        var bossMaps = new HashSet<short>();
        foreach (var boss in bosses)
        {
            var templateMatches = monsters.Where(monster =>
                monster.SourceMapId == boss.MapId &&
                string.Equals(
                    monster.TemplateKey,
                    boss.TemplateKey,
                    StringComparison.Ordinal)).ToArray();
            var template = templateMatches.Length == 1
                ? templateMatches[0]
                : null;
            if (!mapIds.Contains(boss.MapId) ||
                !IsGameplayText(boss.SceneKey, 96) ||
                !IsGameplayText(boss.TemplateKey, 128) ||
                !IsGameplayText(boss.DisplayName, 255) ||
                boss.BonusBasisPoints is < 0 or > 100_000 ||
                boss.RespawnInterval <= TimeSpan.Zero ||
                boss.RespawnInterval > TimeSpan.FromDays(30) ||
                template is null ||
                !template.IsBoss ||
                template.IsElite ||
                template.IsPet ||
                !string.Equals(
                    template.Rank,
                    "boss",
                    StringComparison.OrdinalIgnoreCase) ||
                template.DisplayName.StartsWith(
                    "[Elite]",
                    StringComparison.OrdinalIgnoreCase) ||
                template.DisplayName.StartsWith(
                    "[E]",
                    StringComparison.OrdinalIgnoreCase) ||
                !bossMaps.Add(boss.MapId))
            {
                throw Invalid("gameplay", "A gameplay world boss is invalid.");
            }
        }

        var pending = MaterializeGameplay(
                content.PendingWorldBossAreas,
                MaximumGameplayWorldBosses,
                "pending world-boss area")
            .OrderBy(static value => value.MapId)
            .ToArray();
        var pendingMaps = new HashSet<short>();
        foreach (var area in pending)
        {
            if (!mapIds.Contains(area.MapId) ||
                bossMaps.Contains(area.MapId) ||
                !pendingMaps.Add(area.MapId) ||
                !IsGameplayText(area.SceneKey, 96) ||
                !IsGameplayText(area.Reason, MaximumGameplayTextLength))
            {
                throw Invalid(
                    "gameplay",
                    "A pending gameplay world-boss area is invalid.");
            }
        }

        var progression = PinGameplayProgression(content);

        return new GameplayContentCatalog(
            maps,
            addressPoints,
            links,
            monsters,
            bosses,
            pending,
            progression.Skills)
        {
            Classes = progression.Classes,
            TalentEffects = progression.TalentEffects,
            Talents = progression.Talents,
            SkillBooks = progression.SkillBooks
        };
    }

    private static T[] MaterializeGameplay<T>(
        IReadOnlyList<T> source,
        int maximumCount,
        string description)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > maximumCount)
        {
            throw Invalid(
                "gameplay",
                $"Gameplay {description} count exceeds {maximumCount}.");
        }

        return source.ToArray();
    }

    private static bool IsGameplayText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static bool IsOptionalGameplayText(
        string? value,
        int maximumLength) =>
        value is not null && value.Length <= maximumLength;
}
