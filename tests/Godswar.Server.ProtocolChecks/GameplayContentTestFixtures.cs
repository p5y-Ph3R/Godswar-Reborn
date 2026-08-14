using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Test-only publication boundary for the captured generated declarations.
/// Production gameplay reads the equivalent immutable catalog from PostgreSQL.
/// </summary>
internal static class GameplayContentTestFixtures
{
    public static GameplayContentCatalog Published { get; } = Create();

    public static GameplayRuntimeCatalogs Runtime { get; } =
        GameplayRuntimeCatalogs.Create(Published);

    private static GameplayContentCatalog Create() =>
        new GameplayContentCatalog(
            MapTemplateSeeds.Maps
                .Select(static map => new GameplayMapDefinition(
                    map.MapId,
                    map.SceneKey,
                    map.DisplayName,
                    map.ClientSceneId,
                    map.MapMode))
                .ToArray(),
            MapTemplateSeeds.AddressPoints
                .Select(static point =>
                    new GameplayMapAddressPointDefinition(
                        point.MapId,
                        point.GroupIndex,
                        point.PointIndex,
                        point.GroupName,
                        point.Name,
                        point.X,
                        point.Z,
                        point.Source))
                .ToArray(),
            CreateLinks(),
            MonsterTemplateSeeds.Monsters
                .Select(static monster =>
                    new GameplayMonsterTemplateDefinition(
                        monster.SourceKey,
                        monster.SourceKind,
                        monster.SourceMapId,
                        monster.SceneKey,
                        monster.TemplateKey,
                        monster.DisplayName,
                        monster.Rank,
                        monster.IsBoss,
                        monster.IsElite,
                        monster.IsPet,
                        monster.AttackType,
                        monster.CollisionRange))
                .ToArray(),
            CreateWorldBosses(),
            [
                new GameplayPendingWorldBossArea(
                    68,
                    "Parnassus",
                    "Outdoor faction area; requires a new neutral boss " +
                    "because its Athenian and Spartan Generals are " +
                    "opposing-faction quest objectives.")
            ],
            SkillTalentSeeds.Skills
                .Select(static skill =>
                    new GameplaySkillCombatDefinition(
                        skill.SkillId,
                        skill.Target,
                        skill.AffectObj,
                        (float)skill.Distance,
                        (float)skill.Range,
                        skill.Property,
                        skill.Mp,
                        skill.Power1,
                        skill.Power2,
                        TimeSpan.FromSeconds(
                            (double)skill.IntonateTime),
                        TimeSpan.FromSeconds(
                            (double)skill.CoolingTime))
                    {
                        DisplayName = skill.DisplayName,
                        BaseName = skill.BaseName,
                        SkillLevel = skill.SkillLevel,
                        ClassIds = skill.ClassIds,
                        PreviousSkillId = skill.PreviousSkillId,
                        MinLevel = skill.MinLevel,
                        MaxLevel = skill.MaxLevel,
                        Description = skill.Description,
                        StatsJson = skill.StatsJson
                    })
                .ToArray())
        {
            Classes = SkillTalentSeeds.Classes
                .Select(static value => new GameplayClassDefinition(
                    value.Id,
                    value.Name,
                    value.DisplayName,
                    value.Source))
                .ToArray(),
            TalentEffects = SkillTalentSeeds.TalentEffects
                .Select(static value => new GameplayTalentEffectDefinition(
                    value.Id,
                    value.Key,
                    value.DisplayName,
                    value.Percent))
                .ToArray(),
            Talents = SkillTalentSeeds.Talents
                .Select(static value => new GameplayTalentDefinition(
                    value.Id,
                    value.ClassId,
                    value.TreeOrder,
                    value.Name,
                    value.PrefixId,
                    value.RequiredPrefixRank,
                    value.RequiredTotalRank,
                    value.EquipRequest,
                    value.EffectType,
                    value.EffectId,
                    value.EffectValue,
                    value.IsPercent,
                    value.IconX,
                    value.IconY,
                    value.IconWidth,
                    value.IconHeight,
                    value.StatsJson))
                .ToArray(),
            SkillBooks = SkillTalentSeeds.SkillBooks
                .Select(static value => new GameplaySkillBookDefinition(
                    value.ItemId,
                    value.NameKey,
                    value.DisplayName,
                    value.SkillId,
                    value.BaseName,
                    value.SkillLevel,
                    value.ClassIds,
                    value.MinLevel,
                    value.MaxLevel,
                    value.PreviousSkillId,
                    value.StatsJson))
                .ToArray()
        };

    private static IReadOnlyList<GameplayMapLinkDefinition> CreateLinks()
    {
        var links = new List<GameplayMapLinkDefinition>();
        var seen = new HashSet<(short, short, float, float)>();
        foreach (var link in MapTemplateSeeds.Links)
        {
            if (!seen.Add((link.MapId, link.TargetMapId, link.X, link.Z)))
            {
                continue;
            }

            var disabled = link.MapId == 6 &&
                           link.TargetMapId is 9 or 15;
            links.Add(new GameplayMapLinkDefinition(
                link.MapId,
                link.LinkIndex,
                link.TargetMapId,
                link.X,
                link.Z,
                link.Source,
                disabled
                    ? GameplayMapLinkConfidence
                        .ExcludedByObservedTopology
                    : GameplayMapLinkConfidence.CapturedSpanMap,
                disabled
                    ? GameplayMapLinkActivation.DisabledByWorldTopology
                    : GameplayMapLinkActivation.Automatic,
                disabled
                    ? "Disabled walking edge: observed world topology " +
                      "permits Mycenae access only through Olympia."
                    : "Captured SpanMap boundary with a matching reciprocal."));
        }

        links.AddRange(
        [
            AddressLink(6, 7, -198f, 0f, "Mycenae_All", "Olympia"),
            AddressLink(7, 6, 212f, -104f, "Olympia_All", "Mycenae"),
            AddressLink(7, 20, -181f, 226f, "Olympia_All", "Delphi Forest"),
            AddressLink(20, 7, 132f, -224f, "Oracle_of_Delphi_All", "Olympia"),
            AddressLink(20, 10, -200f, -4f, "Oracle_of_Delphi_All", "Larissa"),
            AddressLink(10, 20, 216f, -68f, "Larissa_All", "Delphi Forest"),
            AddressLink(10, 22, -195f, 150f, "Larissa_All", "Elasson"),
            AddressLink(22, 10, 208f, -16f, "Elasson_All", "Larissa"),
            AddressLink(22, 21, -208f, 124f, "Elasson_All", "Olympus"),
            AddressLink(21, 22, 212f, 80f, "Olympus_All", "Elasson")
        ]);
        return links;
    }

    private static GameplayMapLinkDefinition AddressLink(
        short sourceMapId,
        short targetMapId,
        float x,
        float z,
        string sceneKey,
        string label) =>
        new(
            sourceMapId,
            LinkIndex: 0,
            targetMapId,
            x,
            z,
            $"./Localization/en_us/Monster/{sceneKey}/Address.ini",
            GameplayMapLinkConfidence.ReciprocalAddressPoint,
            GameplayMapLinkActivation.Automatic,
            $"Exact '{label}' address point paired with its reciprocal map label.");

    private static IReadOnlyList<GameplayWorldBossDefinition>
        CreateWorldBosses()
    {
        var interval = TimeSpan.FromHours(12);
        const int bonusBasisPoints = 2_500;
        return
        [
            new(3, "Parnitha_1", "A_boss_boar_001", "Boar King Tomas", bonusBasisPoints, interval),
            new(5, "Nemea_1", "A_boss_wolf_005", "Astrien", bonusBasisPoints, interval),
            new(6, "Mycenae_All", "A_boss_kingofscorpion_001", "[BOSS]Darkmist", bonusBasisPoints, interval),
            new(7, "Olympia_All", "C_boss_centaur_001", "Centaur Leader", bonusBasisPoints, interval),
            new(8, "Thermopylae_All", "B_bossB_xerxes_001", "Mardonius", bonusBasisPoints, interval),
            new(9, "Thebes_All", "A_boss_kingofscorpiondi_001", "[BOSS]Scorpion Lord Selket", bonusBasisPoints, interval),
            new(10, "Larissa_All", "C_boss_dragon_014", "Little Demate", bonusBasisPoints, interval),
            new(11, "Marathon_All", "A_boss_bull_001", "Minos the Bull King", bonusBasisPoints, interval),
            new(12, "Parnitha_2", "B_bossB_octopus_001", "Naga Siren Eirsigel", bonusBasisPoints, interval),
            new(13, "Peloponnese_All", "A_boss_spider_008", "Spider Queen Ala", bonusBasisPoints, interval),
            new(14, "Nemea_2", "B_bossB_spriggan_001", "Evil Treant Falio", bonusBasisPoints, interval),
            new(15, "Derveni_All", "B_boss_centaur_001", "Centaur Shaikh Hailer", bonusBasisPoints, interval),
            new(16, "Argolis_All", "A_boss_amazon_004", "Leader Cassirer", bonusBasisPoints, interval),
            new(17, "Isthmus_of_Corinth_All", "B_boss_dragon_001", "Red Dragon Puluo", bonusBasisPoints, interval),
            new(18, "Megara_All", "A_boss_mage_018", "Lord Barryonyx", bonusBasisPoints, interval),
            new(19, "Plataea_All", "B_boss_cyclops_001", "Giant Alcyoneus", bonusBasisPoints, interval),
            new(20, "Oracle_of_Delphi_All", "A_boss_long_005", "Hydra Lord Xausa", bonusBasisPoints, interval),
            new(21, "Olympus_All", "C_boss_dragon_013", "Bahamut", bonusBasisPoints, interval),
            new(22, "Elasson_All", "C_boss_dragon_002", "Ice Dragon", bonusBasisPoints, interval)
        ];
    }
}
