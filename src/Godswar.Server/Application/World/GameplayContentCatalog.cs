namespace Godswar.Server.Application.World;

/// <summary>
/// Process-pinned gameplay configuration loaded from PostgreSQL in the same
/// repeatable-read snapshot as the published world content. The database is
/// authoritative; this catalog is an immutable in-process copy.
/// </summary>
internal sealed record GameplayContentCatalog(
    IReadOnlyList<GameplayMapDefinition> Maps,
    IReadOnlyList<GameplayMapAddressPointDefinition> AddressPoints,
    IReadOnlyList<GameplayMapLinkDefinition> Links,
    IReadOnlyList<GameplayMonsterTemplateDefinition> MonsterTemplates,
    IReadOnlyList<GameplayWorldBossDefinition> WorldBosses,
    IReadOnlyList<GameplayPendingWorldBossArea> PendingWorldBossAreas,
    IReadOnlyList<GameplaySkillCombatDefinition> SkillCombatDefinitions)
{
    public IReadOnlyList<GameplayClassDefinition> Classes { get; init; } = [];

    public IReadOnlyList<GameplayTalentEffectDefinition> TalentEffects
        { get; init; } = [];

    public IReadOnlyList<GameplayTalentDefinition> Talents { get; init; } = [];

    public IReadOnlyList<GameplaySkillBookDefinition> SkillBooks
        { get; init; } = [];

    public static GameplayContentCatalog Empty { get; } = new(
        [],
        [],
        [],
        [],
        [],
        [],
        []);
}

internal sealed record GameplayMapDefinition(
    short MapId,
    string SceneKey,
    string DisplayName,
    int? ClientSceneId);

internal sealed record GameplayMapAddressPointDefinition(
    short MapId,
    short GroupIndex,
    short PointIndex,
    string GroupName,
    string Name,
    float X,
    float Z,
    string Source);

internal sealed record GameplayMapLinkDefinition(
    short SourceMapId,
    short LinkIndex,
    short TargetMapId,
    float X,
    float Z,
    string Source,
    GameplayMapLinkConfidence Confidence,
    GameplayMapLinkActivation Activation,
    string Note);

internal enum GameplayMapLinkConfidence
{
    CapturedSpanMap = 0,
    ReciprocalAddressPoint = 1,
    ExcludedByObservedTopology = 2
}

internal enum GameplayMapLinkActivation
{
    Automatic = 0,
    DisabledByWorldTopology = 1
}

internal sealed record GameplayMonsterTemplateDefinition(
    string SourceKey,
    string SourceKind,
    short? SourceMapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    string Rank,
    bool IsBoss,
    bool IsElite,
    bool IsPet,
    float? CollisionRange);

internal sealed record GameplayWorldBossDefinition(
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    int BonusBasisPoints,
    TimeSpan RespawnInterval);

internal sealed record GameplayPendingWorldBossArea(
    short MapId,
    string SceneKey,
    string Reason);

internal sealed record GameplaySkillCombatDefinition(
    int SkillId,
    int Target,
    int AffectObj,
    float Distance,
    float Range,
    int Property,
    int Mp,
    decimal Power1,
    decimal Power2,
    TimeSpan CastTime,
    TimeSpan Cooldown)
{
    public string DisplayName { get; init; } = string.Empty;

    public string BaseName { get; init; } = string.Empty;

    public short? SkillLevel { get; init; }

    public IReadOnlyList<short> ClassIds { get; init; } = [];

    public int? PreviousSkillId { get; init; }

    public int? MinLevel { get; init; }

    public int? MaxLevel { get; init; }

    public string Description { get; init; } = string.Empty;

    public string StatsJson { get; init; } = "{}";
}

internal sealed record GameplayClassDefinition(
    short Id,
    string Name,
    string DisplayName,
    string Source);

internal sealed record GameplayTalentEffectDefinition(
    short Id,
    string Key,
    string DisplayName,
    bool Percent);

internal sealed record GameplayTalentDefinition(
    int Id,
    short ClassId,
    short TreeOrder,
    string Name,
    int PrefixId,
    int RequiredPrefixRank,
    int RequiredTotalRank,
    int EquipRequest,
    string EffectType,
    short EffectId,
    decimal EffectValue,
    bool IsPercent,
    int IconX,
    int IconY,
    int IconWidth,
    int IconHeight,
    string StatsJson);

internal sealed record GameplaySkillBookDefinition(
    int ItemId,
    string NameKey,
    string DisplayName,
    int SkillId,
    string BaseName,
    short? SkillLevel,
    IReadOnlyList<short> ClassIds,
    int? MinLevel,
    int? MaxLevel,
    int? PreviousSkillId,
    string StatsJson);
