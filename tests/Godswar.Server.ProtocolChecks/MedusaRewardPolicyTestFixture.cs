using Godswar.Server.Application.WorldInstances;
using System.Runtime.CompilerServices;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaRewardPolicyTestFixture
{
    [ModuleInitializer]
    public static void Install()
    {
        MedusaRewardPolicyCatalog.Install(Create());
        MedusaMonsterContentCatalog.Install(CreateMonsterContent());
    }

    public static MedusaRewardPolicySnapshot Create() => new(
        [
            Title(
                MedusaEncounterTitle.MedusaChallengers,
                MedusaTitleAwardPolicy.ChallengersKey,
                "Medusa Challengers",
                5011,
                300),
            Title(
                MedusaEncounterTitle.MedusaSlayers,
                MedusaTitleAwardPolicy.SlayersKey,
                "Medusa Slayers",
                5010,
                200),
            Title(
                MedusaEncounterTitle.MedusaExecutioners,
                MedusaTitleAwardPolicy.ExecutionersKey,
                "Medusa Executioners",
                5009,
                100),
            Title(
                MedusaEncounterTitle.GorgonBreaker,
                MedusaTitleAwardPolicy.GorgonBreakerKey,
                "Gorgon Breaker",
                5154,
                400),
            Title(
                MedusaEncounterTitle.BaneOfTheThreeSisters,
                MedusaTitleAwardPolicy.BaneOfTheThreeSistersKey,
                "Bane of the Three Sisters",
                5153,
                500),
            Title(
                MedusaEncounterTitle.HeirOfPerseus,
                MedusaTitleAwardPolicy.HeirOfPerseusKey,
                "Heir of Perseus",
                5152,
                600)
        ],
        [
            .. Incomplete(
                MedusaEncounterDifficulty.Normal,
                [(0, 300), (950, 375), (1200, 450), (1500, 525),
                    (1700, 600), (1900, 675), (2200, 750)]),
            .. Completed(
                MedusaEncounterDifficulty.Normal,
                [(600, 1350, null), (900, 1275, null),
                    (1200, 1200, null), (1500, 1125, null),
                    (1800, 1050, null), (2400, 975, null)]),
            .. Incomplete(
                MedusaEncounterDifficulty.Enhanced,
                [(0, 300), (950, 600), (1200, 750), (1500, 900),
                    (1700, 1050), (1900, 1200), (2200, 1350)]),
            .. Completed(
                MedusaEncounterDifficulty.Enhanced,
                [
                    (600, 2250,
                        MedusaEncounterTitle.MedusaChallengers),
                    (900, 2175, MedusaEncounterTitle.MedusaSlayers),
                    (1200, 2100,
                        MedusaEncounterTitle.MedusaExecutioners),
                    (1500, 2025, null), (1800, 1950, null),
                    (2400, 1800, null)
                ]),
            .. Incomplete(
                MedusaEncounterDifficulty.Mythic,
                [(0, 450), (950, 900), (1200, 1125), (1500, 1350),
                    (1700, 1575), (1900, 1800), (2200, 2025)]),
            .. Completed(
                MedusaEncounterDifficulty.Mythic,
                [
                    (600, 3375, MedusaEncounterTitle.HeirOfPerseus),
                    (900, 3300,
                        MedusaEncounterTitle.BaneOfTheThreeSisters),
                    (1200, 3150, MedusaEncounterTitle.GorgonBreaker),
                    (1500, 3075, null), (1800, 2925, null),
                    (2400, 2700, null)
                ])
        ]);

    private static MedusaTitleDefinition Title(
        MedusaEncounterTitle title,
        string semanticKey,
        string displayName,
        uint clientTitleId,
        int bonus) => new(
        title,
        new MedusaTitleSemanticKey(semanticKey),
        displayName,
        clientTitleId,
        new(bonus, bonus, bonus, bonus));

    private static IEnumerable<MedusaRewardRule> Incomplete(
        MedusaEncounterDifficulty difficulty,
        IEnumerable<(int Score, int Points)> rows) =>
        rows.Select(row => new MedusaRewardRule(
            difficulty,
            MedusaRewardRuleKind.IncompleteScore,
            row.Score,
            row.Points,
            null));

    private static IEnumerable<MedusaRewardRule> Completed(
        MedusaEncounterDifficulty difficulty,
        IEnumerable<(int Seconds, int Points, MedusaEncounterTitle? Title)>
            rows) =>
        rows.Select(row => new MedusaRewardRule(
            difficulty,
            MedusaRewardRuleKind.CompletedTime,
            row.Seconds,
            row.Points,
            row.Title));

    private static MedusaMonsterContentSnapshot CreateMonsterContent()
    {
        var rules = new List<MedusaMonsterRule>();
        var loot = new List<MedusaMonsterLootRule>();
        foreach (var difficulty in
                 MedusaIslandEncounterPolicy.Difficulties)
        {
            foreach (var template in MedusaIslandRosterPolicy.Templates)
            {
                var health = checked(
                    NormalHealthFor(template.Alias) *
                    (uint)difficulty.HealthMultiplier);
                var level = LevelFor(template.Alias);
                rules.Add(new(
                    difficulty.Difficulty,
                    template.Alias,
                    level,
                    checked((uint)health),
                    ScoreFor(template.Alias, template.Rank),
                    MovementFor(template.Alias),
                    template.Alias is
                        MedusaIslandRosterTemplateAliases.Stheno or
                        MedusaIslandRosterTemplateAliases.Medusa
                            ? null
                            : 4_200,
                    template.Alias is
                        MedusaIslandRosterTemplateAliases.Stheno or
                        MedusaIslandRosterTemplateAliases.Medusa
                            ? null
                            : 20_000,
                    PetExperienceFor(level)));
            }

            AddLoot(loot, difficulty.Difficulty);
        }
        return new(rules, loot);
    }

    private static void AddLoot(
        ICollection<MedusaMonsterLootRule> loot,
        MedusaEncounterDifficulty difficulty)
    {
        loot.Add(Loot(difficulty,
            MedusaIslandRosterTemplateAliases.MudCrocodile,
            0, 10001, 500));
        loot.Add(Loot(difficulty,
            MedusaIslandRosterTemplateAliases.JungleDeer,
            0, 12030, 500));
        loot.Add(Loot(difficulty,
            MedusaIslandRosterTemplateAliases.PikemanA,
            0, 12010, 500));
        loot.Add(Loot(difficulty,
            MedusaIslandRosterTemplateAliases.PikemanB,
            0, 12010, 500));
        loot.Add(Loot(difficulty,
            MedusaIslandRosterTemplateAliases.Stheno,
            0, 9941, 10_000));
        loot.Add(Loot(difficulty,
            MedusaIslandRosterTemplateAliases.Stheno,
            1, 9941, 10_000));
        loot.Add(Loot(difficulty,
            MedusaIslandRosterTemplateAliases.Medusa,
            0, 9941, 10_000));
        loot.Add(Loot(difficulty,
            MedusaIslandRosterTemplateAliases.Medusa,
            1, 9940, 10_000));
        loot.Add(new(
            difficulty,
            MedusaIslandRosterTemplateAliases.Medusa,
            2,
            9916,
            10_000,
            6,
            6));
    }

    private static MedusaMonsterLootRule Loot(
        MedusaEncounterDifficulty difficulty,
        string alias,
        int index,
        uint itemId,
        int chanceBasisPoints) => new(
        difficulty,
        alias,
        index,
        itemId,
        chanceBasisPoints,
        1,
        1);

    private static uint NormalHealthFor(string alias) => alias switch
    {
        MedusaIslandRosterTemplateAliases.Stheno => 3_000_000,
        MedusaIslandRosterTemplateAliases.Euryale => 5_000_000,
        MedusaIslandRosterTemplateAliases.Chrysaor => 2_000_000,
        MedusaIslandRosterTemplateAliases.Medusa => 3_500_000,
        MedusaIslandRosterTemplateAliases.EliteArcher or
            MedusaIslandRosterTemplateAliases.EliteCrazyAxemanA or
            MedusaIslandRosterTemplateAliases.EliteShamanSix or
            MedusaIslandRosterTemplateAliases.EliteShamanEight => 2_500_000,
        MedusaIslandRosterTemplateAliases.EliteCrazyAxemanC or
            MedusaIslandRosterTemplateAliases.EliteGuardianB or
            MedusaIslandRosterTemplateAliases.ElitePriestB12 or
            MedusaIslandRosterTemplateAliases.EliteShamanC9 or
            MedusaIslandRosterTemplateAliases.EliteShamanC8 => 800_000,
        MedusaIslandRosterTemplateAliases.EliteJungleWizardB or
            MedusaIslandRosterTemplateAliases.EliteGorgonPriestC14 => 500_000,
        MedusaIslandRosterTemplateAliases.EliteGorgonWizard or
            MedusaIslandRosterTemplateAliases.EliteCyclopsSwordsman =>
                8_000_000,
        MedusaIslandRosterTemplateAliases.ElitePriestA12 => 250_000,
        _ when alias.StartsWith("elite-", StringComparison.Ordinal) =>
            1_500_000,
        _ when alias.StartsWith("normal-", StringComparison.Ordinal) =>
            800_000,
        _ => throw new ArgumentOutOfRangeException(nameof(alias), alias, null)
    };

    private static int ScoreFor(string alias, MedusaMonsterRank rank) =>
        alias switch
        {
            MedusaIslandRosterTemplateAliases.Stheno => 1_000,
            MedusaIslandRosterTemplateAliases.Medusa => 1_100,
            MedusaIslandRosterTemplateAliases.Euryale or
                MedusaIslandRosterTemplateAliases.Chrysaor => 50,
            _ when rank == MedusaMonsterRank.Elite => 50,
            _ => 1
        };

    private static int MovementFor(string alias) => alias switch
    {
        MedusaIslandRosterTemplateAliases.Euryale => 7_368,
        MedusaIslandRosterTemplateAliases.Stheno or
            MedusaIslandRosterTemplateAliases.Medusa => 5_000,
        _ => 10_000
    };

    private static uint LevelFor(string alias) => alias switch
    {
        MedusaIslandRosterTemplateAliases.Stheno or
            MedusaIslandRosterTemplateAliases.Medusa => 200,
        MedusaIslandRosterTemplateAliases.Euryale => 130,
        MedusaIslandRosterTemplateAliases.Chrysaor or
            MedusaIslandRosterTemplateAliases.EliteGorgonDemon or
            MedusaIslandRosterTemplateAliases.EliteJungleWizardC5 or
            MedusaIslandRosterTemplateAliases.EliteJungleWizardC6 or
            MedusaIslandRosterTemplateAliases.EliteDarkPriest or
            MedusaIslandRosterTemplateAliases.EliteHammerSoldier or
            MedusaIslandRosterTemplateAliases.EliteJungleWizardB or
            MedusaIslandRosterTemplateAliases.ElitePriestB12 or
            MedusaIslandRosterTemplateAliases.EliteShamanC9 or
            MedusaIslandRosterTemplateAliases.EliteGorgonPriestC14 => 100,
        _ => 95
    };

    private static int PetExperienceFor(uint level) => level switch
    {
        95 => 524,
        100 => 548,
        130 => 712,
        200 => 1_116,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };
}
