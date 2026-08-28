using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaIslandRosterPolicyChecks
{
    public const string CheckName =
        "Medusa Island fixed roster, native skills, and client templates";

    public static Task RunAsync()
    {
        CheckFixedRosterAndScore();
        CheckEliteGroupLayout();
        CheckNativeMechanics();
        CheckTemplatePairs();
        CheckFailClosedLookups();
        CheckImmutableCoordinateFreeContract();
        return Task.CompletedTask;
    }

    private static void CheckFixedRosterAndScore()
    {
        var spawns = MedusaIslandRosterPolicy.Spawns;
        Check.Equal(136, spawns.Length, "captured hostile spawn count");
        Check.Equal(136, spawns.Select(spawn => spawn.SpawnId).Distinct().Count(),
            "every spawn identity is unique");
        Check.Equal(102, spawns.Count(spawn =>
            spawn.Kind == MedusaIslandRosterSpawnKind.Ordinary),
            "ordinary monster count");
        Check.Equal(0, spawns.Count(spawn =>
            spawn.Kind == MedusaIslandRosterSpawnKind.UtilityCarrier),
            "the captured roster has no invented utility rank");
        Check.Equal(102, spawns.Count(spawn =>
            spawn.Rank == MedusaMonsterRank.Normal),
            "total normal-rank count");
        Check.Equal(30, spawns.Count(spawn =>
            spawn.Rank == MedusaMonsterRank.Elite),
            "elite count");
        Check.Equal(4, spawns.Count(spawn =>
            spawn.Rank == MedusaMonsterRank.Boss),
            "boss count");
        Check.Equal(3_802, MedusaIslandRosterPolicy.TotalVictoryScore,
            "the captured per-monster scores total 3,802");

        Check.Equal(102, spawns.Count(spawn =>
            spawn.EncounterRole == MedusaEncounterEnemyRole.Ordinary),
            "ordinary encounter roles match the captured normal roster");
        Check.Equal(0, spawns.Count(spawn =>
            spawn.EncounterRole == MedusaEncounterEnemyRole.UtilityCarrier),
            "captured monsters do not receive an artificial utility role");
        Check.Equal(30, spawns.Count(spawn =>
            spawn.EncounterRole == MedusaEncounterEnemyRole.Elite),
            "elite encounter roles are explicit");

        var bossRoles = spawns.Where(spawn =>
                spawn.Rank == MedusaMonsterRank.Boss)
            .Select(spawn => spawn.EncounterRole)
            .Order()
            .ToArray();
        Check.True(bossRoles.SequenceEqual([
                MedusaEncounterEnemyRole.Euryale,
                MedusaEncounterEnemyRole.Chrysaor,
                MedusaEncounterEnemyRole.Stheno,
                MedusaEncounterEnemyRole.Medusa]),
            "all four bosses retain distinct encounter roles");
    }

    private static void CheckEliteGroupLayout()
    {
        var spawns = MedusaIslandRosterPolicy.Spawns;
        Check.True(
            MedusaIslandRosterPolicy.Groups.IsEmpty &&
            spawns.All(spawn => spawn.EliteGroupId is null),
            "the capture is represented directly without invented numbered groups");

        Check.True(
            Island(MedusaIslandRosterIsland.First) is var first &&
            first.Length == 54 &&
            first.Count(spawn => spawn.Rank == MedusaMonsterRank.Normal) == 30 &&
            first.Count(spawn => spawn.Rank == MedusaMonsterRank.Elite) == 22 &&
            first.Count(spawn => spawn.Rank == MedusaMonsterRank.Boss) == 2,
            "the first component contains the captured 54-hostile composition");
        Check.True(
            Island(MedusaIslandRosterIsland.Second) is var second &&
            second.Length == 72 &&
            second.Count(spawn => spawn.Rank == MedusaMonsterRank.Normal) == 70 &&
            second.Count(spawn => spawn.Rank == MedusaMonsterRank.Elite) == 2,
            "the second component contains 70 axemen and two captured elites");

        Check.True(
            Spawn("Euryale").Anchor ==
                MedusaIslandRosterAnchor.FirstIslandTopLeft &&
            Spawn("Chrysaor").Anchor ==
                MedusaIslandRosterAnchor.FirstIslandTopRight &&
            MedusaIslandRosterPolicy.Spawns
                .Where(spawn => spawn.SpawnId is not "Euryale" and
                    not "Chrysaor")
                .All(spawn =>
                    spawn.Anchor == MedusaIslandRosterAnchor.None),
            "Euryale and Chrysaor retain their first-component traversal anchors");

        Check.True(
            Spawn("E2-Elite").TemplateAlias ==
                MedusaIslandRosterTemplateAliases.EliteMudCrocodile &&
            Spawn("First-Normal-03").TemplateAlias ==
                MedusaIslandRosterTemplateAliases.MudCrocodile &&
            Spawn("E13-Elite").TemplateAlias ==
                MedusaIslandRosterTemplateAliases.EliteGorgonDemon &&
            Spawn("E16-Elite").TemplateAlias ==
                MedusaIslandRosterTemplateAliases.EliteDarkPriest,
            "captured crocodile and second-component elite identities are preserved");

        var final = Island(MedusaIslandRosterIsland.Final);
        Check.Equal(10, final.Length,
            "final component contains two bosses and eight escorts");
        Check.Equal(2, final.Count(spawn =>
            spawn.Rank == MedusaMonsterRank.Normal),
            "final component has exactly two normal escorts");
        Check.Equal(2, final.Count(spawn =>
            spawn.Rank == MedusaMonsterRank.Boss),
            "final component has Stheno and Medusa");
        Check.Equal(6, final.Count(spawn =>
            spawn.Rank == MedusaMonsterRank.Elite),
            "final component has the six captured elite escorts");
    }

    private static void CheckNativeMechanics()
    {
        CheckSkill(MedusaIslandRosterMechanic.Stun,
            2002, 330, 180, 2, 1);
        CheckSkill(MedusaIslandRosterMechanic.Freeze,
            2018, 402, 180, 3, 1);
        CheckSkill(MedusaIslandRosterMechanic.Bleed,
            2041, 18, 204, 15, 1);
        CheckSkill(MedusaIslandRosterMechanic.Shackle,
            2017, 401, 180, 3, 1);
        CheckSkill(MedusaIslandRosterMechanic.OutgoingPhysicalAmplifier,
            2082, 236, 0, 30, 10);
        CheckSkill(MedusaIslandRosterMechanic.OutgoingMagicalAmplifier,
            2080, 235, 0, 30, 10);

        CheckLaneMechanic(
            MedusaIslandRosterLane.Stun,
            MedusaIslandRosterMechanic.Stun);
        CheckLaneMechanic(
            MedusaIslandRosterLane.Freeze,
            MedusaIslandRosterMechanic.Freeze);
        CheckLaneMechanic(
            MedusaIslandRosterLane.Bleed,
            MedusaIslandRosterMechanic.Bleed);

        Check.True(Spawn("Euryale").Skill is
                { Mechanic: MedusaIslandRosterMechanic.Shackle,
                  SkillId: 2017, StatusId: 401 },
            "Euryale applies the native full-disable Shackle");
        Check.True(Spawn("Chrysaor").Skill is
                { Mechanic: MedusaIslandRosterMechanic.Bleed,
                  SkillId: 2041, StatusId: 18 },
            "Chrysaor uses the selected native AOE(1)+Bleed definition");

        var finalUtility = MedusaIslandRosterPolicy.Spawns.Where(spawn =>
                spawn.Island == MedusaIslandRosterIsland.Final &&
                spawn.Skill?.Mechanic is
                    MedusaIslandRosterMechanic.OutgoingPhysicalAmplifier or
                    MedusaIslandRosterMechanic.OutgoingMagicalAmplifier)
            .ToArray();
        Check.Equal(2, finalUtility.Count(spawn => spawn.Skill is
            { Mechanic: MedusaIslandRosterMechanic.OutgoingPhysicalAmplifier,
              SkillId: 2082, StatusId: 236,
              OutgoingDamageMultiplier: 10 }),
            "both Pikemen refresh the 30-second 10x physical amplifier");
        Check.Equal(2, finalUtility.Count(spawn => spawn.Skill is
            { Mechanic: MedusaIslandRosterMechanic.OutgoingMagicalAmplifier,
              SkillId: 2080, StatusId: 235,
              OutgoingDamageMultiplier: 10 }),
            "both Axemen refresh the 30-second 10x magical amplifier");
        Check.True(finalUtility.All(spawn =>
                spawn.Skill!.Value.Duration == TimeSpan.FromSeconds(30)),
            "all final utility boosts last exactly 30 seconds");

        var nativeAmplifierScenes = new short[] { 209, 223 };
        Check.True(finalUtility.All(spawn =>
                spawn.Skill is { } skill &&
                skill.HasNativeClientSceneRestriction &&
                skill.NativeAffectedClientSceneIds.SequenceEqual(
                    nativeAmplifierScenes) &&
                skill.CanUseUnmodifiedNativeStatusInClientScene(209) &&
                skill.CanUseUnmodifiedNativeStatusInClientScene(223) &&
                !skill.CanUseUnmodifiedNativeStatusInClientScene(216)),
            "stock amplifier statuses retain their exact Medusa client-scene restriction");

        Check.True(
            MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                200, out var enhancedClientSceneId) &&
            enhancedClientSceneId == 209 &&
            MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                204, out var normalClientSceneId) &&
            normalClientSceneId == 223 &&
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                209, out _),
            "MapIdToName client-scene identities map content 200/204 to AffectMap 209/223 without conflating namespaces");

        Check.True(MedusaIslandRosterPolicy.Skills
                .Where(skill => skill.Mechanic is not
                    MedusaIslandRosterMechanic.OutgoingPhysicalAmplifier and
                    not MedusaIslandRosterMechanic.OutgoingMagicalAmplifier)
                .All(skill =>
                    !skill.HasNativeClientSceneRestriction &&
                    skill.CanUseUnmodifiedNativeStatusInClientScene(209) &&
                    skill.CanUseUnmodifiedNativeStatusInClientScene(223)),
            "unrestricted authored status bindings remain available in both Medusa client scenes");

        Check.True(MedusaIslandRosterPolicy.Spawns
                .Where(spawn => spawn.Skill.HasValue)
                .All(spawn =>
                    spawn.Skill!.Value.ApplicationRule ==
                        (spawn.Skill.Value.Mechanic is
                            MedusaIslandRosterMechanic.Stun or
                            MedusaIslandRosterMechanic.Freeze or
                            MedusaIslandRosterMechanic.Shackle
                                ? MedusaIslandStatusApplicationRule
                                    .DeterministicRatingProcOnCommittedHit
                                : MedusaIslandStatusApplicationRule
                                    .GuaranteedOnCommittedHit) &&
                    !spawn.Skill.Value.UsesNativeStatusOddsAsProbability),
            "stun, freeze, and shackle use the shared rating proc while other authored statuses remain guaranteed without treating StatusOdds as a percent");
    }

    private static void CheckFailClosedLookups()
    {
        Check.True(!MedusaIslandRosterPolicy.TryGetGroup(0, out _) &&
                   !MedusaIslandRosterPolicy.TryGetGroup(21, out _),
            "unknown elite groups fail closed");
        Check.True(!MedusaIslandRosterPolicy.TryGetSpawn(null, out _) &&
                   !MedusaIslandRosterPolicy.TryGetSpawn("", out _) &&
                   !MedusaIslandRosterPolicy.TryGetSpawn("E21-Elite", out _),
            "unknown spawn identities fail closed");
        Check.True(!MedusaIslandRosterPolicy.TryGetTemplatePair(null, out _) &&
                   !MedusaIslandRosterPolicy.TryGetTemplatePair(
                       "unknown-template", out _),
            "unknown template aliases fail closed");
        Check.True(!MedusaIslandRosterPolicy.TryResolveTemplate(
                       (MedusaEncounterDifficulty)byte.MaxValue,
                       MedusaIslandRosterTemplateAliases.Medusa, out _) &&
                   !MedusaIslandRosterPolicy.TryResolveTemplate(
                       MedusaEncounterDifficulty.Normal,
                       "unknown-template", out _) &&
                   !MedusaIslandRosterPolicy.TryResolveTemplateByMap(
                       201, MedusaIslandRosterTemplateAliases.Medusa, out _),
            "unknown difficulty, template, and map resolution fail closed");
        Check.True(!MedusaIslandRosterPolicy.TryGetSkillBinding(
                (MedusaIslandRosterMechanic)byte.MaxValue, out _),
            "unknown skill mechanics fail closed");
    }

    private static void CheckImmutableCoordinateFreeContract()
    {
        Check.True(!MedusaIslandRosterPolicy.Spawns.IsDefault &&
                   !MedusaIslandRosterPolicy.Groups.IsDefault &&
                   !MedusaIslandRosterPolicy.Templates.IsDefault &&
                   MedusaIslandRosterPolicy.Groups.All(group =>
                       !group.OrdinaryEscortSpawnIds.IsDefault),
            "roster, groups, templates, and escort lists are immutable arrays");

        var propertyNames = typeof(MedusaIslandRosterSpawn).GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Check.True(!propertyNames.Any(name =>
                name.Contains("Coordinate", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Position", StringComparison.OrdinalIgnoreCase) ||
                name is "X" or "Y" or "Z"),
            "roster contract publishes no uncertified coordinates");
    }

    private static void CheckLaneMechanic(
        MedusaIslandRosterLane lane,
        MedusaIslandRosterMechanic mechanic)
    {
        var members = MedusaIslandRosterPolicy.Spawns.Where(spawn =>
            spawn.Lane == lane).ToArray();
        Check.True(
            members.Length > 0 &&
            members.All(member => member.Skill?.Mechanic == mechanic),
            $"every captured {lane} member applies {mechanic}");
    }

    private static void CheckSkill(
        MedusaIslandRosterMechanic mechanic,
        int skillId,
        uint statusId,
        int statusOdds,
        int seconds,
        int multiplier)
    {
        Check.True(MedusaIslandRosterPolicy.TryGetSkillBinding(
                mechanic, out var binding) &&
            binding.SkillId == skillId &&
            binding.StatusId == statusId &&
            binding.NativeStatusOddsRating == statusOdds &&
            binding.Duration == TimeSpan.FromSeconds(seconds) &&
            binding.OutgoingDamageMultiplier == multiplier &&
            binding.ApplicationRule == MedusaIslandStatusApplicationRule
                .DeterministicRatingProcOnCommittedHit ==
                    (mechanic is MedusaIslandRosterMechanic.Stun or
                        MedusaIslandRosterMechanic.Freeze or
                        MedusaIslandRosterMechanic.Shackle) &&
            !binding.UsesNativeStatusOddsAsProbability,
            $"{mechanic} preserves native IDs and authored hit semantics");
    }

    private static void CheckResolvedTemplate(
        MedusaEncounterDifficulty difficulty,
        short expectedMapId,
        string expectedScene,
        string expectedKey,
        MedusaIslandRosterTemplatePair pair)
    {
        Check.True(MedusaIslandRosterPolicy.TryResolveTemplate(
                difficulty, pair.Alias, out var resolved) &&
            resolved.MapId == expectedMapId &&
            resolved.SceneKey == expectedScene &&
            resolved.TemplateKey == expectedKey &&
            resolved.DisplayName == pair.DisplayName &&
            resolved.Rank == pair.Rank &&
            resolved.ClientAttackType == pair.ClientAttackType,
            $"{difficulty} resolves exact {pair.Alias} client identity");

        var seed = MonsterTemplateSeeds.Monsters.SingleOrDefault(candidate =>
            candidate.SourceMapId == expectedMapId &&
            candidate.TemplateKey == expectedKey);
        Check.True(seed.TemplateKey == expectedKey &&
                   seed.SceneKey == expectedScene &&
                   seed.DisplayName == pair.DisplayName &&
                   seed.AttackType == pair.ClientAttackType &&
                   seed.Rank == pair.Rank.ToString().ToLowerInvariant(),
            $"map {expectedMapId} contains exact generated template {expectedKey}");
    }

    private static MedusaIslandRosterSpawn[] Island(
        MedusaIslandRosterIsland island) =>
        MedusaIslandRosterPolicy.Spawns.Where(spawn =>
            spawn.Island == island).ToArray();

    private static MedusaIslandRosterSpawn Spawn(string id)
    {
        Check.True(MedusaIslandRosterPolicy.TryGetSpawn(id, out var spawn),
            $"spawn {id} resolves");
        return spawn;
    }
}
