namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Authoritative authored combat and title policy for Medusa Island. This is
/// deliberately separate from <see cref="MedusaIslandPolicy"/>, which retains
/// conflicting source evidence. Runtime integration can consume this policy
/// without rewriting that evidence history.
/// </summary>
internal static class MedusaIslandEncounterPolicy
{
    public const bool ScalesByPartySize = false;
    public const int FullIncomingDamageBasisPoints = 10_000;
    public const int ReducedIncomingDamageBasisPoints = 1_000;

    private const int NormalMapId = 204;
    private const int EnhancedContentMapId = 200;

    private static readonly IReadOnlyList<MedusaEncounterDifficultyDefinition>
        AuthoredDifficulties =
            Array.AsReadOnly<MedusaEncounterDifficultyDefinition>(
            [
                Difficulty(
                    MedusaEncounterDifficulty.Normal,
                    "Normal",
                    "Medusa Island (Normal)",
                    NormalMapId,
                    "Medusa_Island2",
                    healthMultiplier: 1,
                    ordinaryAttack: new(6_000, 5_000),
                    utilityAttack: new(5_700, 4_700),
                    eliteAttack: new(6_400, 5_400),
                    euryaleMagicAttack: 9_000,
                    chrysaorPhysicalAttack: 10_000,
                    sthenoPhysicalAttack: 10_000,
                    medusaMagicAttack: 9_000),
                Difficulty(
                    MedusaEncounterDifficulty.Enhanced,
                    "Enhanced",
                    "Medusa Island (Enhanced)",
                    EnhancedContentMapId,
                    "Medusa_Island",
                    healthMultiplier: 2,
                    ordinaryAttack: new(6_300, 5_300),
                    utilityAttack: new(6_000, 5_000),
                    eliteAttack: new(6_800, 5_800),
                    euryaleMagicAttack: 11_000,
                    chrysaorPhysicalAttack: 12_000,
                    sthenoPhysicalAttack: 13_000,
                    medusaMagicAttack: 12_000),
                Difficulty(
                    MedusaEncounterDifficulty.Mythic,
                    "Mythic",
                    "Mythic Medusa Island",
                    EnhancedContentMapId,
                    "Medusa_Island",
                    healthMultiplier: 5,
                    ordinaryAttack: new(7_000, 5_900),
                    utilityAttack: new(6_600, 5_500),
                    eliteAttack: new(7_600, 6_500),
                    euryaleMagicAttack: 13_500,
                    chrysaorPhysicalAttack: 14_500,
                    sthenoPhysicalAttack: 16_000,
                    medusaMagicAttack: 15_000)
            ]);

    public static IReadOnlyList<MedusaEncounterDifficultyDefinition>
        Difficulties => AuthoredDifficulties;

    public static IReadOnlyList<MedusaEncounterTitleAward> Titles =>
        MedusaRewardPolicyCatalog.Current.CompletionTitles;

    /// <summary>
    /// Converts the stock client identity to its public authored difficulty.
    /// Mythic has no stock-client variant and must arrive as explicit run
    /// metadata instead of being inferred from the shared content map.
    /// </summary>
    public static bool TryResolveLegacyVariant(
        MedusaIslandVariant variant,
        out MedusaEncounterDifficulty difficulty)
    {
        switch (variant)
        {
            case MedusaIslandVariant.Normal:
                difficulty = MedusaEncounterDifficulty.Normal;
                return true;
            case MedusaIslandVariant.Advanced:
                difficulty = MedusaEncounterDifficulty.Enhanced;
                return true;
            default:
                difficulty = default;
                return false;
        }
    }

    public static bool TryGetDifficulty(
        MedusaEncounterDifficulty difficulty,
        out MedusaEncounterDifficultyDefinition definition)
    {
        foreach (var candidate in AuthoredDifficulties)
        {
            if (candidate.Difficulty == difficulty)
            {
                definition = candidate;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    /// <summary>
    /// Resolves only when a content map identifies exactly one authored
    /// difficulty. Map 200 deliberately fails because Enhanced and Mythic
    /// share it; callers must retain the run's explicit difficulty metadata.
    /// </summary>
    public static bool TryGetUniqueDifficultyByContentMap(
        short mapId,
        out MedusaEncounterDifficultyDefinition definition)
    {
        MedusaEncounterDifficultyDefinition? match = null;
        foreach (var candidate in AuthoredDifficulties)
        {
            if (candidate.ContentMapId.Value != mapId)
            {
                continue;
            }

            if (match is not null)
            {
                definition = null!;
                return false;
            }

            match = candidate;
        }

        definition = match!;
        return match is not null;
    }

    public static int TotalEnemyCount(
        MedusaEncounterDifficultyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Enemies.Sum(enemy => enemy.Count);
    }

    public static long TotalMaximumHealth(
        MedusaEncounterDifficultyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Enemies.Sum(
            enemy => (long)enemy.Count * enemy.MaximumHealth);
    }

    public static int TotalVictoryScore(
        MedusaEncounterDifficultyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return MedusaIslandRosterPolicy.Spawns.Sum(spawn =>
            MedusaMonsterContentCatalog.Current.TryGetMonster(
                definition.Difficulty,
                spawn.TemplateAlias,
                out var rule)
                    ? rule.Score
                    : throw new InvalidDataException(
                        $"Missing Medusa score rule for {spawn.TemplateAlias}."));
    }

    public static int IncomingDamageBasisPoints(
        MedusaEncounterEnemyRole boss,
        MedusaDamageChannel channel) =>
        (boss, channel) switch
        {
            (MedusaEncounterEnemyRole.Stheno, MedusaDamageChannel.Magical) =>
                ReducedIncomingDamageBasisPoints,
            (MedusaEncounterEnemyRole.Medusa, MedusaDamageChannel.Physical) =>
                ReducedIncomingDamageBasisPoints,
            (MedusaEncounterEnemyRole.Stheno, MedusaDamageChannel.Physical) or
            (MedusaEncounterEnemyRole.Medusa, MedusaDamageChannel.Magical) =>
                FullIncomingDamageBasisPoints,
            (MedusaEncounterEnemyRole.Stheno, _) or
            (MedusaEncounterEnemyRole.Medusa, _) =>
                throw new ArgumentOutOfRangeException(nameof(channel)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(boss),
                "Incoming channel modifiers are defined only for Stheno and Medusa.")
        };

    /// <summary>
    /// Returns at most one title. Thresholds are inclusive and ordered from
    /// strongest to weakest so a faster completion never receives every title.
    /// </summary>
    public static bool TryResolveBestCompletionTitle(
        MedusaEncounterDifficulty difficulty,
        int teamScore,
        TimeSpan completionTime,
        out MedusaEncounterTitleAward award)
    {
        if (completionTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(completionTime));
        }
        if (!MedusaIslandPolicy.HasVictoryScore(teamScore))
        {
            award = default;
            return false;
        }

        foreach (var candidate in Titles)
        {
            if (candidate.Difficulty == difficulty &&
                completionTime <= candidate.MaximumCompletionTime)
            {
                award = candidate;
                return true;
            }
        }

        award = default;
        return false;
    }

    private static MedusaEncounterDifficultyDefinition Difficulty(
        MedusaEncounterDifficulty difficulty,
        string difficultyLabel,
        string publicName,
        short mapId,
        string sceneKey,
        int healthMultiplier,
        MedusaAttackRatings ordinaryAttack,
        MedusaAttackRatings utilityAttack,
        MedusaAttackRatings eliteAttack,
        int euryaleMagicAttack,
        int chrysaorPhysicalAttack,
        int sthenoPhysicalAttack,
        int medusaMagicAttack) =>
        new(
            difficulty,
            difficultyLabel,
            publicName,
            new(mapId),
            sceneKey,
            healthMultiplier,
            Array.AsReadOnly<MedusaEncounterEnemyDefinition>(
            [
                Enemy(
                    MedusaEncounterEnemyRole.Ordinary,
                    MedusaMonsterRank.Normal,
                    count: 102,
                    normalHealth: 75_000,
                    healthMultiplier,
                    ordinaryAttack),
                Enemy(
                    MedusaEncounterEnemyRole.UtilityCarrier,
                    MedusaMonsterRank.Normal,
                    count: 0,
                    normalHealth: 250_000,
                    healthMultiplier,
                    utilityAttack),
                Enemy(
                    MedusaEncounterEnemyRole.Elite,
                    MedusaMonsterRank.Elite,
                    count: 30,
                    normalHealth: 300_000,
                    healthMultiplier,
                    eliteAttack),
                Enemy(
                    MedusaEncounterEnemyRole.Euryale,
                    MedusaMonsterRank.Boss,
                    count: 1,
                    normalHealth: 750_000,
                    healthMultiplier,
                    new(0, euryaleMagicAttack)),
                Enemy(
                    MedusaEncounterEnemyRole.Chrysaor,
                    MedusaMonsterRank.Boss,
                    count: 1,
                    normalHealth: 875_000,
                    healthMultiplier,
                    new(chrysaorPhysicalAttack, 0)),
                Enemy(
                    MedusaEncounterEnemyRole.Stheno,
                    MedusaMonsterRank.Boss,
                    count: 1,
                    normalHealth: 3_000_000,
                    healthMultiplier,
                    new(sthenoPhysicalAttack, 0)),
                Enemy(
                    MedusaEncounterEnemyRole.Medusa,
                    MedusaMonsterRank.Boss,
                    count: 1,
                    normalHealth: 3_500_000,
                    healthMultiplier,
                    new(0, medusaMagicAttack))
            ]));

    private static MedusaEncounterEnemyDefinition Enemy(
        MedusaEncounterEnemyRole role,
        MedusaMonsterRank rank,
        int count,
        int normalHealth,
        int healthMultiplier,
        MedusaAttackRatings attackRatings) =>
        new(
            role,
            rank,
            count,
            checked(normalHealth * healthMultiplier),
            attackRatings);

}
