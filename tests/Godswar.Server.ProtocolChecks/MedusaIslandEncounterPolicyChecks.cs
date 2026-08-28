using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaIslandEncounterPolicyChecks
{
    public const string CheckName =
        "Medusa Island authored combat, resistance, and title policy";

    public static Task RunAsync()
    {
        Check.True(
            MedusaMonsterContentCatalog.Current.Monsters
                .Select(static rule => rule.Level)
                .Distinct()
                .Order()
                .SequenceEqual([95u, 100u, 130u, 200u]),
            "database monster content retains the captured level tiers");
        CheckDifficultyIdentity();
        CheckFixedRosterAndScore();
        CheckHealthBudgets();
        CheckAttackRatings();
        CheckBossDamageChannels();
        CheckBestCompletionTitles();
        CheckImmutableCollections();
        return Task.CompletedTask;
    }

    private static void CheckDifficultyIdentity()
    {
        Check.Equal(
            3,
            MedusaIslandEncounterPolicy.Difficulties.Count,
            "Normal, Enhanced, and Mythic are authored");
        Check.True(
            MedusaIslandEncounterPolicy.Difficulties.Select(
                    difficulty => difficulty.DifficultyLabel)
                .SequenceEqual(["Normal", "Enhanced", "Mythic"]),
            "public difficulty names do not expose legacy Advanced wording");
        Check.True(
            MedusaIslandEncounterPolicy.Difficulties.Select(
                    difficulty => difficulty.PublicName)
                .SequenceEqual(
                [
                    "Medusa Island (Normal)",
                    "Medusa Island (Enhanced)",
                    "Mythic Medusa Island"
                ]),
            "full player-facing encounter names include Mythic Medusa Island");

        var normal = Profile(MedusaEncounterDifficulty.Normal);
        var enhanced = Profile(MedusaEncounterDifficulty.Enhanced);
        var mythic = Profile(MedusaEncounterDifficulty.Mythic);
        Check.True(
            normal.ContentMapId.Value == 204 &&
            normal.SceneKey == "Medusa_Island2" &&
            enhanced.ContentMapId.Value == 200 &&
            enhanced.SceneKey == "Medusa_Island" &&
            mythic.ContentMapId.Value == 200 &&
            mythic.SceneKey == "Medusa_Island",
            "Mythic explicitly reuses Enhanced map content");

        Check.True(
            MedusaIslandEncounterPolicy.TryResolveLegacyVariant(
                MedusaIslandVariant.Normal,
                out var normalCompatibility) &&
            normalCompatibility == MedusaEncounterDifficulty.Normal &&
            MedusaIslandEncounterPolicy.TryResolveLegacyVariant(
                MedusaIslandVariant.Advanced,
                out var enhancedCompatibility) &&
            enhancedCompatibility == MedusaEncounterDifficulty.Enhanced &&
            !MedusaIslandEncounterPolicy.TryResolveLegacyVariant(
                (MedusaIslandVariant)byte.MaxValue,
                out _),
            "legacy client Advanced resolves explicitly to public Enhanced");

        Check.True(
            MedusaIslandEncounterPolicy.TryGetUniqueDifficultyByContentMap(
                204,
                out var mapNormal) &&
            mapNormal.Difficulty == MedusaEncounterDifficulty.Normal &&
            !MedusaIslandEncounterPolicy.TryGetUniqueDifficultyByContentMap(
                200,
                out _) &&
            !MedusaIslandEncounterPolicy.TryGetUniqueDifficultyByContentMap(
                203,
                out _),
            "map-only difficulty lookup fails closed for shared map 200");
        Check.True(
            !MedusaIslandEncounterPolicy.TryGetDifficulty(
                (MedusaEncounterDifficulty)byte.MaxValue,
                out _),
            "unknown authored difficulties do not borrow a profile");
    }

    private static void CheckFixedRosterAndScore()
    {
        Check.True(
            !MedusaIslandEncounterPolicy.ScalesByPartySize,
            "Medusa combat does not scale with party size");

        var expectedRoles = new[]
        {
            MedusaEncounterEnemyRole.Ordinary,
            MedusaEncounterEnemyRole.UtilityCarrier,
            MedusaEncounterEnemyRole.Elite,
            MedusaEncounterEnemyRole.Euryale,
            MedusaEncounterEnemyRole.Chrysaor,
            MedusaEncounterEnemyRole.Stheno,
            MedusaEncounterEnemyRole.Medusa
        };
        var expectedRanks = new[]
        {
            MedusaMonsterRank.Normal,
            MedusaMonsterRank.Normal,
            MedusaMonsterRank.Elite,
            MedusaMonsterRank.Boss,
            MedusaMonsterRank.Boss,
            MedusaMonsterRank.Boss,
            MedusaMonsterRank.Boss
        };
        var expectedCounts = new[] { 102, 0, 30, 1, 1, 1, 1 };

        foreach (var difficulty in MedusaIslandEncounterPolicy.Difficulties)
        {
            Check.True(
                difficulty.Enemies.Select(enemy => enemy.Role)
                    .SequenceEqual(expectedRoles) &&
                difficulty.Enemies.Select(enemy => enemy.Rank)
                    .SequenceEqual(expectedRanks) &&
                difficulty.Enemies.Select(enemy => enemy.Count)
                    .SequenceEqual(expectedCounts),
                $"{difficulty.PublicName} retains the fixed authored roster");
            Check.Equal(
                136,
                MedusaIslandEncounterPolicy.TotalEnemyCount(difficulty),
                $"{difficulty.PublicName} has 136 enemies");
            Check.Equal(
                3_802,
                MedusaIslandEncounterPolicy.TotalVictoryScore(difficulty),
                $"{difficulty.PublicName} roster has the captured 3,802 points");
        }
    }

    private static void CheckHealthBudgets()
    {
        var normalHealth = new[]
        {
            75_000,
            250_000,
            300_000,
            750_000,
            875_000,
            3_000_000,
            3_500_000
        };
        var normal = Profile(MedusaEncounterDifficulty.Normal);
        var enhanced = Profile(MedusaEncounterDifficulty.Enhanced);
        var mythic = Profile(MedusaEncounterDifficulty.Mythic);

        Check.True(
            normal.HealthMultiplier == 1 &&
            enhanced.HealthMultiplier == 2 &&
            mythic.HealthMultiplier == 5,
            "difficulty health multipliers are fixed at 1x, 2x, and 5x");
        Check.True(
            normal.Enemies.Select(enemy => enemy.MaximumHealth)
                .SequenceEqual(normalHealth) &&
            enhanced.Enemies.Select(enemy => enemy.MaximumHealth)
                .SequenceEqual(normalHealth.Select(health => health * 2)) &&
            mythic.Enemies.Select(enemy => enemy.MaximumHealth)
                .SequenceEqual(normalHealth.Select(health => health * 5)),
            "every role applies its difficulty health multiplier exactly");
        Check.True(
            MedusaIslandEncounterPolicy.TotalMaximumHealth(normal) ==
                24_775_000L &&
            MedusaIslandEncounterPolicy.TotalMaximumHealth(enhanced) ==
                49_550_000L &&
            MedusaIslandEncounterPolicy.TotalMaximumHealth(mythic) ==
                123_875_000L,
            "fixed total health budgets match the approved encounter totals");
    }

    private static void CheckAttackRatings()
    {
        CheckAttacks(
            MedusaEncounterDifficulty.Normal,
            physical: [6_000, 5_700, 6_400, 0, 10_000, 10_000, 0],
            magical: [5_000, 4_700, 5_400, 9_000, 0, 0, 9_000]);
        CheckAttacks(
            MedusaEncounterDifficulty.Enhanced,
            physical: [6_300, 6_000, 6_800, 0, 12_000, 13_000, 0],
            magical: [5_300, 5_000, 5_800, 11_000, 0, 0, 12_000]);
        CheckAttacks(
            MedusaEncounterDifficulty.Mythic,
            physical: [7_000, 6_600, 7_600, 0, 14_500, 16_000, 0],
            magical: [5_900, 5_500, 6_500, 13_500, 0, 0, 15_000]);
    }

    private static void CheckBossDamageChannels()
    {
        Check.True(
            MedusaIslandEncounterPolicy.IncomingDamageBasisPoints(
                MedusaEncounterEnemyRole.Stheno,
                MedusaDamageChannel.Physical) == 10_000 &&
            MedusaIslandEncounterPolicy.IncomingDamageBasisPoints(
                MedusaEncounterEnemyRole.Stheno,
                MedusaDamageChannel.Magical) == 1_000 &&
            MedusaIslandEncounterPolicy.IncomingDamageBasisPoints(
                MedusaEncounterEnemyRole.Medusa,
                MedusaDamageChannel.Physical) == 1_000 &&
            MedusaIslandEncounterPolicy.IncomingDamageBasisPoints(
                MedusaEncounterEnemyRole.Medusa,
                MedusaDamageChannel.Magical) == 10_000,
            "Stheno and Medusa retain 100% correct-channel and 10% wrong-channel damage");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandEncounterPolicy.IncomingDamageBasisPoints(
                MedusaEncounterEnemyRole.Euryale,
                MedusaDamageChannel.Magical),
            "non-final bosses cannot borrow final-boss channel resistance");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandEncounterPolicy.IncomingDamageBasisPoints(
                MedusaEncounterEnemyRole.Medusa,
                (MedusaDamageChannel)byte.MaxValue),
            "unknown damage channels fail closed");
    }

    private static void CheckBestCompletionTitles()
    {
        Check.Equal(
            6,
            MedusaIslandEncounterPolicy.Titles.Count,
            "only the three Enhanced and three Mythic title tiers exist");
        Check.True(
            MedusaIslandEncounterPolicy.Titles.All(title =>
                title.Difficulty != MedusaEncounterDifficulty.Normal),
            "Normal completion has no timing title");

        CheckTitle(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(10),
            MedusaEncounterTitle.MedusaChallengers,
            "Medusa Challengers");
        CheckTitle(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(10).Add(TimeSpan.FromTicks(1)),
            MedusaEncounterTitle.MedusaSlayers,
            "Medusa Slayers");
        CheckTitle(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(15),
            MedusaEncounterTitle.MedusaSlayers,
            "Medusa Slayers");
        CheckTitle(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(20),
            MedusaEncounterTitle.MedusaExecutioners,
            "Medusa Executioners");
        CheckTitle(
            MedusaEncounterDifficulty.Mythic,
            TimeSpan.FromMinutes(10),
            MedusaEncounterTitle.HeirOfPerseus,
            "Heir of Perseus");
        CheckTitle(
            MedusaEncounterDifficulty.Mythic,
            TimeSpan.FromMinutes(15),
            MedusaEncounterTitle.BaneOfTheThreeSisters,
            "Bane of the Three Sisters");
        CheckTitle(
            MedusaEncounterDifficulty.Mythic,
            TimeSpan.FromMinutes(20),
            MedusaEncounterTitle.GorgonBreaker,
            "Gorgon Breaker");

        Check.True(
            !MedusaIslandEncounterPolicy.TryResolveBestCompletionTitle(
                MedusaEncounterDifficulty.Normal,
                3_000,
                TimeSpan.FromMinutes(5),
                out _) &&
            !MedusaIslandEncounterPolicy.TryResolveBestCompletionTitle(
                MedusaEncounterDifficulty.Enhanced,
                3_000,
                TimeSpan.FromMinutes(20).Add(TimeSpan.FromTicks(1)),
                out _) &&
            !MedusaIslandEncounterPolicy.TryResolveBestCompletionTitle(
                (MedusaEncounterDifficulty)byte.MaxValue,
                3_000,
                TimeSpan.FromMinutes(10),
                out _),
            "Normal, late, and unknown completions award no title");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandEncounterPolicy.TryResolveBestCompletionTitle(
                MedusaEncounterDifficulty.Enhanced,
                3_000,
                TimeSpan.FromTicks(-1),
                out _),
            "negative completion times cannot award a title");
    }

    private static void CheckImmutableCollections()
    {
        var normal = Profile(MedusaEncounterDifficulty.Normal);
        Check.Throws<NotSupportedException>(
            () => ((IList<MedusaEncounterDifficultyDefinition>)
                    MedusaIslandEncounterPolicy.Difficulties)
                .Add(normal),
            "difficulty definitions are immutable");
        Check.Throws<NotSupportedException>(
            () => ((IList<MedusaEncounterEnemyDefinition>)normal.Enemies)
                .Add(normal.Enemies[0]),
            "enemy definitions are immutable");
        Check.Throws<NotSupportedException>(
            () => ((IList<MedusaEncounterTitleAward>)
                    MedusaIslandEncounterPolicy.Titles)
                .Add(MedusaIslandEncounterPolicy.Titles[0]),
            "title definitions are immutable");
    }

    private static void CheckAttacks(
        MedusaEncounterDifficulty difficulty,
        int[] physical,
        int[] magical)
    {
        var profile = Profile(difficulty);
        Check.True(
            profile.Enemies.Select(enemy => enemy.AttackRatings.Physical)
                .SequenceEqual(physical) &&
            profile.Enemies.Select(enemy => enemy.AttackRatings.Magical)
                .SequenceEqual(magical),
            $"{difficulty} attack ratings match the authored table");
    }

    private static void CheckTitle(
        MedusaEncounterDifficulty difficulty,
        TimeSpan completionTime,
        MedusaEncounterTitle expectedTitle,
        string expectedDisplayName)
    {
        Check.True(
            MedusaIslandEncounterPolicy.TryResolveBestCompletionTitle(
                difficulty,
                MedusaIslandPolicy.VictoryScore,
                completionTime,
                out var award) &&
            award.Title == expectedTitle &&
            award.DisplayName == expectedDisplayName,
            $"{difficulty} {completionTime} awards only {expectedDisplayName}");
    }

    private static MedusaEncounterDifficultyDefinition Profile(
        MedusaEncounterDifficulty difficulty)
    {
        Check.True(
            MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var profile),
            $"{difficulty} profile resolves");
        return profile;
    }
}
