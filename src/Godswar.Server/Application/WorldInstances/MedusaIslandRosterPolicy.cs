using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Captured Medusa Island roster and stock-client identity bindings.
/// </summary>
internal static partial class MedusaIslandRosterPolicy
{
    public const int EliteGroupCount = 0;
    public const int OrdinaryCount = 102;
    public const int UtilityCarrierCount = 0;
    public const int NormalRankCount = 102;
    public const int EliteCount = 30;
    public const int BossCount = 4;
    public const int TotalSpawnCount = 136;
    public const short EnhancedClientSceneId = 209;
    public const short NormalClientSceneId = 223;

    private static readonly MedusaIslandRosterSkillBinding Stun =
        Binding(MedusaIslandRosterMechanic.Stun, 2002,
            "Non AOE+Stun", 330, "Stuned", 180, 2,
            applicationRule: MedusaIslandStatusApplicationRule
                .DeterministicRatingProcOnCommittedHit);

    private static readonly MedusaIslandRosterSkillBinding Freeze =
        Binding(MedusaIslandRosterMechanic.Freeze, 2018,
            "Non AOE+Freeze", 402, "Frozen", 180, 3,
            applicationRule: MedusaIslandStatusApplicationRule
                .DeterministicRatingProcOnCommittedHit);

    // Chrysaor and the bleed lane both use this exact native AOE bleed.
    private static readonly MedusaIslandRosterSkillBinding Bleed =
        Binding(MedusaIslandRosterMechanic.Bleed, 2041,
            "AOE(1)+Bleed", 18, "Bleeding", 204, 15);

    private static readonly MedusaIslandRosterSkillBinding Shackle =
        Binding(MedusaIslandRosterMechanic.Shackle, 2017,
            "Non AOE+Shackle", 401, "Caged", 180, 3,
            applicationRule: MedusaIslandStatusApplicationRule
                .DeterministicRatingProcOnCommittedHit);

    private static readonly MedusaIslandRosterSkillBinding PhysicalAmplifier =
        Binding(MedusaIslandRosterMechanic.OutgoingPhysicalAmplifier, 2082,
            "Increase physical damage", 236,
            "Increased Physical Damage", 0, 30, 10,
            [EnhancedClientSceneId, NormalClientSceneId]);

    private static readonly MedusaIslandRosterSkillBinding MagicalAmplifier =
        Binding(MedusaIslandRosterMechanic.OutgoingMagicalAmplifier, 2080,
            "Increase magical damage", 235,
            "Increased Magical Damage", 0, 30, 10,
            [EnhancedClientSceneId, NormalClientSceneId]);

    private static readonly ImmutableArray<MedusaIslandRosterSkillBinding>
        AuthoredSkills =
        [Stun, Freeze, Bleed, Shackle, PhysicalAmplifier, MagicalAmplifier];

    private static readonly RosterContent Content = BuildCapturedContent();

    private static readonly FrozenDictionary<int, MedusaIslandEliteGroup>
        GroupsById = Content.Groups.ToFrozenDictionary(group => group.Id);

    private static readonly FrozenDictionary<string, MedusaIslandRosterSpawn>
        SpawnsById = Content.Spawns.ToFrozenDictionary(
            spawn => spawn.SpawnId,
            StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, MedusaIslandRosterTemplatePair>
        TemplatesByAlias = MedusaIslandRosterTemplates.All.ToFrozenDictionary(
            template => template.Alias,
            StringComparer.Ordinal);

    private static readonly FrozenDictionary<MedusaIslandRosterMechanic,
        MedusaIslandRosterSkillBinding> SkillsByMechanic =
        AuthoredSkills.ToFrozenDictionary(skill => skill.Mechanic);

    public static ImmutableArray<MedusaIslandEliteGroup> Groups => Content.Groups;

    public static ImmutableArray<MedusaIslandRosterSpawn> Spawns => Content.Spawns;

    public static ImmutableArray<MedusaIslandRosterTemplatePair> Templates =>
        MedusaIslandRosterTemplates.All;

    public static ImmutableArray<MedusaIslandRosterSkillBinding> Skills =>
        AuthoredSkills;

    public static int TotalVictoryScore => Content.Spawns.Sum(spawn =>
        MedusaMonsterContentCatalog.Current.TryGetMonster(
            MedusaEncounterDifficulty.Enhanced,
            spawn.TemplateAlias,
            out var rule)
                ? rule.Score
                : throw new InvalidDataException(
                    $"Missing Medusa score rule for {spawn.TemplateAlias}."));

    public static bool TryGetGroup(
        int groupId,
        out MedusaIslandEliteGroup group) =>
        GroupsById.TryGetValue(groupId, out group!);

    public static bool TryGetSpawn(
        string? spawnId,
        out MedusaIslandRosterSpawn spawn)
    {
        if (string.IsNullOrWhiteSpace(spawnId))
        {
            spawn = null!;
            return false;
        }

        return SpawnsById.TryGetValue(spawnId, out spawn!);
    }

    public static bool TryGetTemplatePair(
        string? templateAlias,
        out MedusaIslandRosterTemplatePair pair)
    {
        if (string.IsNullOrWhiteSpace(templateAlias))
        {
            pair = default;
            return false;
        }

        return TemplatesByAlias.TryGetValue(templateAlias, out pair);
    }

    public static bool TryGetSkillBinding(
        MedusaIslandRosterMechanic mechanic,
        out MedusaIslandRosterSkillBinding binding) =>
        SkillsByMechanic.TryGetValue(mechanic, out binding);

    /// <summary>
    /// Resolves the second identity from MapIdToNameConfig.ini. Status.ini's
    /// AffectMap field uses this client-scene namespace, not the server's
    /// content-map ID namespace.
    /// </summary>
    public static bool TryResolveClientSceneIdByContentMap(
        short contentMapId,
        out short clientSceneId)
    {
        clientSceneId = contentMapId switch
        {
            200 => EnhancedClientSceneId,
            204 => NormalClientSceneId,
            _ => default
        };
        return clientSceneId != default;
    }

    public static bool TryResolveTemplate(
        MedusaEncounterDifficulty difficulty,
        string? templateAlias,
        out MedusaIslandResolvedTemplate template)
    {
        short mapId;
        string sceneKey;
        switch (difficulty)
        {
            case MedusaEncounterDifficulty.Normal:
                mapId = 204;
                sceneKey = "Medusa_Island2";
                break;
            case MedusaEncounterDifficulty.Enhanced:
            case MedusaEncounterDifficulty.Mythic:
                mapId = 200;
                sceneKey = "Medusa_Island";
                break;
            default:
                template = default;
                return false;
        }

        return TryResolveTemplateCore(
            mapId,
            sceneKey,
            templateAlias,
            out template);
    }

    public static bool TryResolveTemplateByMap(
        int mapId,
        string? templateAlias,
        out MedusaIslandResolvedTemplate template) =>
        mapId switch
        {
            200 => TryResolveTemplateCore(
                200, "Medusa_Island", templateAlias, out template),
            204 => TryResolveTemplateCore(
                204, "Medusa_Island2", templateAlias, out template),
            _ => FailTemplate(out template)
        };

    private static bool TryResolveTemplateCore(
        short mapId,
        string sceneKey,
        string? templateAlias,
        out MedusaIslandResolvedTemplate template)
    {
        if (!TryGetTemplatePair(templateAlias, out var pair))
        {
            template = default;
            return false;
        }

        var templateKey = mapId == 200
            ? pair.EnhancedTemplateKey
            : pair.NormalTemplateKey;
        template = new(
            mapId,
            sceneKey,
            pair.Alias,
            templateKey,
            pair.DisplayName,
            pair.Rank,
            pair.ClientAttackType);
        return true;
    }

    private static bool FailTemplate(out MedusaIslandResolvedTemplate template)
    {
        template = default;
        return false;
    }

    private static RosterContent BuildContent()
    {
        var groups = ImmutableArray.CreateBuilder<MedusaIslandEliteGroup>(
            EliteGroupCount);
        var spawns = ImmutableArray.CreateBuilder<MedusaIslandRosterSpawn>(
            TotalSpawnCount);

        AddGroup(groups, spawns, 1, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Stun,
            MedusaIslandRosterTemplateAliases.EliteMudCrocodile,
            Repeat(MedusaIslandRosterTemplateAliases.MudCrocodile, 3), Stun);
        AddGroup(groups, spawns, 2, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Stun,
            MedusaIslandRosterTemplateAliases.EliteCrazyAxemanA,
            [MedusaIslandRosterTemplateAliases.AxemanA,
             MedusaIslandRosterTemplateAliases.GiantAxeman,
             MedusaIslandRosterTemplateAliases.PikemanB], Stun);
        AddGroup(groups, spawns, 3, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Stun,
            MedusaIslandRosterTemplateAliases.EliteArcher,
            [MedusaIslandRosterTemplateAliases.MudCrocodile,
             MedusaIslandRosterTemplateAliases.JungleDeer,
             MedusaIslandRosterTemplateAliases.AxemanB], Stun);
        AddGroup(groups, spawns, 4, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Stun,
            MedusaIslandRosterTemplateAliases.EliteGuardianA,
            [MedusaIslandRosterTemplateAliases.PikemanA,
             MedusaIslandRosterTemplateAliases.GiantAxeman,
             MedusaIslandRosterTemplateAliases.AxemanA], Stun);

        AddGroup(groups, spawns, 5, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Freeze,
            MedusaIslandRosterTemplateAliases.EliteShamanSix,
            Repeat(MedusaIslandRosterTemplateAliases.Shaman, 3), Freeze);
        AddGroup(groups, spawns, 6, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Freeze,
            MedusaIslandRosterTemplateAliases.EliteShamanEight,
            Repeat(MedusaIslandRosterTemplateAliases.JungleWizard, 3), Freeze);
        AddGroup(groups, spawns, 7, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Freeze,
            MedusaIslandRosterTemplateAliases.EliteJungleWizardC5,
            Repeat(MedusaIslandRosterTemplateAliases.Astrologer, 3), Freeze);
        AddGroup(groups, spawns, 8, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Freeze,
            MedusaIslandRosterTemplateAliases.EliteJungleWizardC6,
            [MedusaIslandRosterTemplateAliases.Shaman,
             MedusaIslandRosterTemplateAliases.JungleWizard,
             MedusaIslandRosterTemplateAliases.Astrologer], Freeze);

        AddGroup(groups, spawns, 9, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Bleed,
            MedusaIslandRosterTemplateAliases.EliteAxeman,
            Repeat(MedusaIslandRosterTemplateAliases.GiantAxeman, 3), Bleed);
        AddGroup(groups, spawns, 10, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Bleed,
            MedusaIslandRosterTemplateAliases.EliteHammerSoldier,
            Repeat(MedusaIslandRosterTemplateAliases.AxemanA, 3), Bleed);
        AddGroup(groups, spawns, 11, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Bleed,
            MedusaIslandRosterTemplateAliases.EliteCrazyAxemanC,
            Repeat(MedusaIslandRosterTemplateAliases.PikemanB, 3), Bleed);
        AddGroup(groups, spawns, 12, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.Bleed,
            MedusaIslandRosterTemplateAliases.EliteGuardianB,
            [MedusaIslandRosterTemplateAliases.MudCrocodile,
             MedusaIslandRosterTemplateAliases.JungleDeer,
             MedusaIslandRosterTemplateAliases.GiantAxeman], Bleed);

        AddGroup(groups, spawns, 13, MedusaIslandRosterIsland.Second,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterTemplateAliases.EliteCyclopsSwordsman,
            [MedusaIslandRosterTemplateAliases.Shaman,
             MedusaIslandRosterTemplateAliases.JungleDeer], null);
        AddGroup(groups, spawns, 14, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterTemplateAliases.EliteDarkShaman, [], null,
            "Euryale", MedusaIslandRosterTemplateAliases.Euryale,
            MedusaEncounterEnemyRole.Euryale, Shackle,
            MedusaIslandRosterAnchor.FirstIslandTopLeft);
        AddGroup(groups, spawns, 15, MedusaIslandRosterIsland.First,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterTemplateAliases.EliteGorgonDemon, [], null,
            "Chrysaor", MedusaIslandRosterTemplateAliases.Chrysaor,
            MedusaEncounterEnemyRole.Chrysaor, Bleed,
            MedusaIslandRosterAnchor.FirstIslandTopRight);
        AddGroup(groups, spawns, 16, MedusaIslandRosterIsland.Second,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterTemplateAliases.EliteJungleWizardB,
            [MedusaIslandRosterTemplateAliases.MudCrocodile,
             MedusaIslandRosterTemplateAliases.GiantAxeman], null);
        AddGroup(groups, spawns, 17, MedusaIslandRosterIsland.Second,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterTemplateAliases.EliteDarkPriest,
            [MedusaIslandRosterTemplateAliases.JungleWizard,
             MedusaIslandRosterTemplateAliases.Astrologer], null);
        AddGroup(groups, spawns, 18, MedusaIslandRosterIsland.Second,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterTemplateAliases.EliteAstrologer,
            [MedusaIslandRosterTemplateAliases.PikemanA,
             MedusaIslandRosterTemplateAliases.AxemanA], null);
        AddGroup(groups, spawns, 19, MedusaIslandRosterIsland.Second,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterTemplateAliases.EliteGorgonWizard,
            [MedusaIslandRosterTemplateAliases.Shaman,
             MedusaIslandRosterTemplateAliases.MudCrocodile], null);

        AddFinalBoss(spawns, "Stheno",
            MedusaIslandRosterTemplateAliases.Stheno,
            MedusaEncounterEnemyRole.Stheno);
        AddFinalBoss(spawns, "Medusa",
            MedusaIslandRosterTemplateAliases.Medusa,
            MedusaEncounterEnemyRole.Medusa);
        AddUtility(spawns, "Final-Pikeman-1",
            MedusaIslandRosterTemplateAliases.PikemanA, PhysicalAmplifier);
        AddUtility(spawns, "Final-Pikeman-2",
            MedusaIslandRosterTemplateAliases.PikemanB, PhysicalAmplifier);
        AddUtility(spawns, "Final-Axeman-1",
            MedusaIslandRosterTemplateAliases.AxemanA, MagicalAmplifier);
        AddUtility(spawns, "Final-Axeman-2",
            MedusaIslandRosterTemplateAliases.AxemanB, MagicalAmplifier);
        AddGroup(groups, spawns, 20, MedusaIslandRosterIsland.Second,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterTemplateAliases.EliteGuardianB, [], null);

        return new(groups.MoveToImmutable(), spawns.MoveToImmutable());
    }

    private static void AddGroup(
        ImmutableArray<MedusaIslandEliteGroup>.Builder groups,
        ImmutableArray<MedusaIslandRosterSpawn>.Builder spawns,
        int id,
        MedusaIslandRosterIsland island,
        MedusaIslandRosterLane lane,
        string eliteTemplate,
        ImmutableArray<string> ordinaryTemplates,
        MedusaIslandRosterSkillBinding? groupSkill,
        string? bossSpawnId = null,
        string? bossTemplate = null,
        MedusaEncounterEnemyRole bossRole = default,
        MedusaIslandRosterSkillBinding? bossSkill = null,
        MedusaIslandRosterAnchor bossAnchor = MedusaIslandRosterAnchor.None)
    {
        var eliteSpawnId = $"E{id}-Elite";
        spawns.Add(new(eliteSpawnId, id, island, lane,
            MedusaIslandRosterSpawnKind.Elite,
            MedusaEncounterEnemyRole.Elite,
            MedusaMonsterRank.Elite, eliteTemplate, groupSkill));

        var escortIds = ImmutableArray.CreateBuilder<string>(
            ordinaryTemplates.Length);
        for (var index = 0; index < ordinaryTemplates.Length; index++)
        {
            var escortId = $"E{id}-Normal-{index + 1}";
            escortIds.Add(escortId);
            spawns.Add(new(escortId, id, island, lane,
                MedusaIslandRosterSpawnKind.Ordinary,
                MedusaEncounterEnemyRole.Ordinary,
                MedusaMonsterRank.Normal,
                ordinaryTemplates[index], groupSkill));
        }

        if (bossSpawnId is not null)
        {
            spawns.Add(new(bossSpawnId, id, island, lane,
                MedusaIslandRosterSpawnKind.Boss, bossRole,
                MedusaMonsterRank.Boss, bossTemplate!, bossSkill,
                bossAnchor));
        }

        groups.Add(new(id, island, lane, eliteSpawnId,
            escortIds.MoveToImmutable(), bossSpawnId));
    }

    private static void AddFinalBoss(
        ImmutableArray<MedusaIslandRosterSpawn>.Builder spawns,
        string spawnId,
        string templateAlias,
        MedusaEncounterEnemyRole role) =>
        spawns.Add(new(spawnId, null, MedusaIslandRosterIsland.Final,
            MedusaIslandRosterLane.None, MedusaIslandRosterSpawnKind.Boss,
            role, MedusaMonsterRank.Boss, templateAlias, null));

    private static void AddUtility(
        ImmutableArray<MedusaIslandRosterSpawn>.Builder spawns,
        string spawnId,
        string templateAlias,
        MedusaIslandRosterSkillBinding skill) =>
        spawns.Add(new(spawnId, null, MedusaIslandRosterIsland.Final,
            MedusaIslandRosterLane.None,
            MedusaIslandRosterSpawnKind.UtilityCarrier,
            MedusaEncounterEnemyRole.UtilityCarrier,
            MedusaMonsterRank.Normal, templateAlias, skill));

    private static ImmutableArray<string> Repeat(string value, int count) =>
        Enumerable.Repeat(value, count).ToImmutableArray();

    private static MedusaIslandRosterSkillBinding Binding(
        MedusaIslandRosterMechanic mechanic,
        int skillId,
        string skillName,
        uint statusId,
        string statusName,
        int statusOdds,
        int seconds,
        int multiplier = 1,
        ImmutableArray<short> nativeAffectedClientSceneIds = default,
        MedusaIslandStatusApplicationRule applicationRule =
            MedusaIslandStatusApplicationRule.GuaranteedOnCommittedHit) =>
        new(mechanic, skillId, skillName, statusId, statusName, statusOdds,
            TimeSpan.FromSeconds(seconds),
            applicationRule,
            multiplier,
            nativeAffectedClientSceneIds.IsDefault
                ? ImmutableArray<short>.Empty
                : nativeAffectedClientSceneIds);

    private sealed record RosterContent(
        ImmutableArray<MedusaIslandEliteGroup> Groups,
        ImmutableArray<MedusaIslandRosterSpawn> Spawns);
}
