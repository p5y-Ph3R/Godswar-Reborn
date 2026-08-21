using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummySkillChecks
{
    private static readonly int[] WarriorScalarDamageSkillIds =
    [
        .. Enumerable.Range(0, 5),
        .. Enumerable.Range(10, 5),
        .. Enumerable.Range(20, 5),
        .. Enumerable.Range(50, 5),
        .. Enumerable.Range(60, 5)
    ];

    private static readonly int[] WarriorAreaDamageSkillIds =
    [
        .. Enumerable.Range(30, 5),
        .. Enumerable.Range(40, 5)
    ];

    private static async Task CheckWarriorDamageSkillsAsync()
    {
        CheckWarriorPublishedPolicy();
        await CheckWarriorLightChopRuntimeAsync();
        await CheckWarriorAreaRuntimeAsync(30);
        await CheckWarriorAreaRuntimeAsync(40);
        await CheckWarriorAuthorityBoundariesAsync();
    }

    private static void CheckWarriorPublishedPolicy()
    {
        CheckProfessionNeutralCatalogPolicy();
        Check.True(
            WarriorScalarDamageSkillIds.All(skillId =>
                TrainingDummyDamageSkillPolicy.IsSupportedScalar(
                    GameplayContentTestFixtures.Runtime,
                    PublishedSkill(skillId),
                    attackerProfession: 0)) &&
            WarriorAreaDamageSkillIds.All(skillId =>
                TrainingDummyDamageSkillPolicy.IsSupportedArea(
                    GameplayContentTestFixtures.Runtime,
                    PublishedSkill(skillId),
                    attackerProfession: 0)),
            "all 25 Warrior scalar and 10 self-area damage ranks are admitted from the published catalog");

        var stun = PublishedSkill(70);
        var exposeArmor = PublishedSkill(80);
        Check.True(
            TrainingDummyDamageSkillPolicy.ValidateScalar(
                GameplayContentTestFixtures.Runtime,
                stun,
                attackerProfession: 0) ==
                TrainingDummySkillRejectionReason.UnsupportedSkill &&
            TrainingDummyDamageSkillPolicy.ValidateArea(
                GameplayContentTestFixtures.Runtime,
                exposeArmor,
                attackerProfession: 0) ==
                TrainingDummySkillRejectionReason.UnsupportedSkill,
            "Warrior status-only skills remain outside the damage adapter");

        var castTimeSkill = GameplayContentTestFixtures.Published
            .SkillCombatDefinitions
            .Select(static value => PublishedSkill(value.SkillId))
            .First(skill =>
                skill.CastTime > TimeSpan.Zero &&
                (SkillCombatResolver.IsHostileMonsterSingleTargetSkill(skill) ||
                 SkillCombatResolver.IsHostileMonsterSelfAreaSkill(skill)) &&
                (skill.Power1 > -1m || skill.Power2 > 0m));
        var castTimeProfession = RequiredProfession(castTimeSkill.SkillId);
        Check.True(
            TrainingDummyDamageSkillPolicy.ValidateScalar(
                GameplayContentTestFixtures.Runtime,
                castTimeSkill,
                castTimeProfession) ==
                TrainingDummySkillRejectionReason.UnsupportedSkill &&
            TrainingDummyDamageSkillPolicy.ValidateArea(
                GameplayContentTestFixtures.Runtime,
                castTimeSkill,
                castTimeProfession) ==
                TrainingDummySkillRejectionReason.UnsupportedSkill,
            "cast-time skills remain outside the instantaneous adapter");

        var groundArea = GameplayContentTestFixtures.Published
            .SkillCombatDefinitions
            .Select(static value => PublishedSkill(value.SkillId))
            .First(SkillCombatResolver.IsHostileMonsterGroundAreaSkill);
        Check.True(
            TrainingDummyDamageSkillPolicy.ValidateArea(
                GameplayContentTestFixtures.Runtime,
                groundArea,
                RequiredProfession(groundArea.SkillId)) ==
                TrainingDummySkillRejectionReason.UnsupportedSkill,
            "ground-targeted areas remain outside the self-centred adapter");
    }

    private static void CheckProfessionNeutralCatalogPolicy()
    {
        var admitted = GameplayContentTestFixtures.Published
            .SkillCombatDefinitions
            .Select(value => new
            {
                Published = value,
                Definition = PublishedSkill(value.SkillId)
            })
            .Where(value =>
                value.Definition.CastTime == TimeSpan.Zero &&
                (value.Definition.Power1 > -1m ||
                 value.Definition.Power2 > 0m) &&
                (SkillCombatResolver.IsHostileMonsterSingleTargetSkill(
                     value.Definition) ||
                 SkillCombatResolver.IsHostileMonsterSelfAreaSkill(
                     value.Definition)))
            .ToArray();
        Check.True(
            admitted.Length > 0 &&
            admitted.All(value => value.Published.ClassIds.Count > 0 &&
                value.Published.ClassIds.All(classId =>
                    SkillCombatResolver.IsHostileMonsterSingleTargetSkill(
                        value.Definition)
                        ? TrainingDummyDamageSkillPolicy.IsSupportedScalar(
                            GameplayContentTestFixtures.Runtime,
                            value.Definition,
                            checked((byte)classId))
                        : TrainingDummyDamageSkillPolicy.IsSupportedArea(
                            GameplayContentTestFixtures.Runtime,
                            value.Definition,
                            checked((byte)classId)))),
            "every published instant scalar or self-area damage skill is admitted for each owning profession");

        var classScoped = admitted.First(value =>
            value.Published.ClassIds.Count < 4);
        var wrongProfession = Enumerable.Range(0, 4)
            .Select(static value => checked((short)value))
            .First(value => !classScoped.Published.ClassIds.Contains(value));
        var rejection = SkillCombatResolver.IsHostileMonsterSingleTargetSkill(
            classScoped.Definition)
            ? TrainingDummyDamageSkillPolicy.ValidateScalar(
                GameplayContentTestFixtures.Runtime,
                classScoped.Definition,
                checked((byte)wrongProfession))
            : TrainingDummyDamageSkillPolicy.ValidateArea(
                GameplayContentTestFixtures.Runtime,
                classScoped.Definition,
                checked((byte)wrongProfession));
        Check.True(
            rejection == TrainingDummySkillRejectionReason.
                AttackerProfessionMismatch,
            "the generic policy verifies catalog ownership without hard-coding a supported profession");
    }

    private static async Task CheckWarriorLightChopRuntimeAsync()
    {
        var attacker = Player(
            8701,
            8701,
            "WarriorScalar",
            0,
            0,
            profession: 0);
        await using var fixture = await Fixture.CreateAsync(attacker: attacker);
        var skill = PublishedSkill(0);
        var beforeHealth = fixture.Target.CurrentHp;
        var beforeMana = fixture.Attacker.CurrentMp;
        var revision = FindHittingRevision(
            fixture.Attacker,
            fixture.Target,
            skill);
        var now = DateTimeOffset.Parse("2026-08-18T00:00:00Z");
        var decision = await fixture.ResolveAsync(
            revision,
            now,
            skill);

        Check.True(
            decision.Accepted &&
            decision.Combat.Resolution.Hit &&
            decision.Combat.AppliedDamage > 0 &&
            fixture.Target.CurrentHp < beforeHealth &&
            fixture.Attacker.CurrentMp == beforeMana - skill.Mp,
            "Warrior Light Chop 1 commits exact-dummy damage and its authoritative MP cost");

        var healthAfterFirst = fixture.Target.CurrentHp;
        var manaAfterFirst = fixture.Attacker.CurrentMp;
        var replayRevisionCalls = 0;
        var replay = await fixture.Registry.ResolveTrainingDummyDamageScalarAsync(
            fixture.AttackerSocket.Session,
            LocalPlayerObjectId,
            fixture.TargetObjectId,
            TrainingSkillCastPacket(
                checked((uint)skill.SkillId),
                fixture.TargetObjectId),
            skill,
            () => ++replayRevisionCalls,
            now.AddSeconds(1),
            CancellationToken.None);
        Check.True(
            replay.RejectionReason ==
                TrainingDummySkillRejectionReason.CooldownActive &&
            replay.CooldownReadyAt == now + skill.Cooldown &&
            replayRevisionCalls == 0 &&
            fixture.Target.CurrentHp == healthAfterFirst &&
            fixture.Attacker.CurrentMp == manaAfterFirst,
            "skill ID 0 replay inside cooldown spends no additional MP, HP, or combat revision");
    }

    private static async Task CheckWarriorAreaRuntimeAsync(int skillId)
    {
        await using var fixture = await AreaFixture.CreateAsync(
            attackerProfession: 0);
        var skill = PublishedSkill(skillId);
        var beforeHealth = fixture.Dummies
            .ToDictionary(static value => value.Id, static value => value.CurrentHp);
        var ordinaryHealth = fixture.Ordinary.CurrentHp;
        var beforeMana = fixture.Attacker.CurrentMp;
        var revision = FindAreaHittingRevision(fixture, skill);
        var decision = await fixture.ResolveAsync(
            skill,
            () => revision,
            DateTimeOffset.Parse("2026-08-18T01:00:00Z")
                .AddMinutes(skillId));

        Check.True(
            decision.Accepted &&
            decision.Combats.Count == fixture.Dummies.Count &&
            decision.Combats.All(static value =>
                value.Resolution.Hit && value.AppliedDamage > 0) &&
            fixture.Dummies.All(value =>
                value.CurrentHp < beforeHealth[value.Id]) &&
            fixture.Attacker.CurrentMp == beforeMana - skill.Mp &&
            fixture.Ordinary.CurrentHp == ordinaryHealth,
            $"Warrior self-area skill {skillId} damages only exact dummies");
    }

    private static async Task CheckWarriorAuthorityBoundariesAsync()
    {
        var warrior = Player(
            8711,
            8711,
            "WarriorBoundary",
            0,
            0,
            profession: 0);
        var skill = PublishedSkill(0);
        await using (var forged = await Fixture.CreateAsync(attacker: warrior))
        {
            var drift = await forged.ResolveAsync(
                1,
                DateTimeOffset.Parse("2026-08-18T02:00:00Z"),
                skill with { Mp = skill.Mp + 1 });
            var wrongProfession = await forged.ResolveAsync(
                1,
                DateTimeOffset.Parse("2026-08-18T02:01:00Z"),
                SpearHit());
            Check.True(
                drift.RejectionReason ==
                    TrainingDummySkillRejectionReason.UnsupportedSkill &&
                wrongProfession.RejectionReason ==
                    TrainingDummySkillRejectionReason.
                        AttackerProfessionMismatch &&
                forged.Attacker.CurrentMp == forged.Attacker.MaxMp,
                "definition drift and cross-profession skill forgery mutate no resources");
        }

        var ordinary = Player(8712, 8712, "OrdinaryTarget", 0, 1);
        await using (var protectedPlayer = await Fixture.CreateAsync(
                         attacker: Player(
                             8713,
                             8713,
                             "WarriorAgainstPlayer",
                             0,
                             0,
                             profession: 0),
                         target: ordinary))
        {
            var beforeHealth = ordinary.CurrentHp;
            var denied = await protectedPlayer.ResolveAsync(
                1,
                DateTimeOffset.Parse("2026-08-18T02:02:00Z"),
                skill);
            Check.True(
                denied.RejectionReason ==
                    TrainingDummySkillRejectionReason.
                        TargetIsNotExactTrainingDummy &&
                ordinary.CurrentHp == beforeHealth,
                "profession-neutral damage routing does not open ordinary player PvP");
        }
    }

    private static SkillCombatDefinition PublishedSkill(int skillId)
    {
        if (GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                skillId,
                out var skill))
        {
            return skill;
        }
        throw new InvalidOperationException(
            $"Published skill {skillId} was not found.");
    }

    private static byte RequiredProfession(int skillId)
    {
        var published = GameplayContentTestFixtures.Published
            .SkillCombatDefinitions
            .Single(value => value.SkillId == skillId);
        return checked((byte)published.ClassIds.First());
    }

    private static long FindAreaHittingRevision(
        AreaFixture fixture,
        in SkillCombatDefinition definition)
    {
        var attacker = CombatCharacterStatsAdapter.FromCharacter(
            fixture.Attacker);
        var skill = TrainingDummyDamageSkillPolicy.Snapshot(definition);
        for (var revision = 1L; revision <= 500; revision++)
        {
            var allHit = fixture.Dummies.All(target =>
            {
                var eventId = CombatEventIdentity.ForPlayerSkill(
                    fixture.Attacker.Id,
                    target.Id,
                    fixture.Attacker.VitalsRevision,
                    target.VitalsRevision,
                    revision,
                    skill.SkillId);
                return PlayerCombatRules.ResolvePvpSkillDamage(
                    attacker,
                    CombatCharacterStatsAdapter.ToTarget(
                        target.Level,
                        target.CalculatedStats!),
                    skill,
                    eventId).Hit;
            });
            if (allHit)
            {
                return revision;
            }
        }
        throw new InvalidOperationException(
            "Expected a deterministic all-target hit within 500 revisions.");
    }
}
