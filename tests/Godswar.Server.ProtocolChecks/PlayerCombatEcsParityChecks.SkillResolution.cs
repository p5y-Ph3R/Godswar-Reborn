using Godswar.Server.Game;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsParityChecks
{
    private static readonly PlayerCombatSkillSnapshot
        ResolutionPhysicalSkill = new(
            SkillId: 8_101,
            Target: 44,
            AffectObject: 28,
            Distance: 5f,
            AreaRadius: 0f,
            ManaCost: 11,
            Property: 0,
            Power1: 0.5m,
            Power2: 50m);

    private static readonly PlayerCombatSkillSnapshot
        ResolutionMagicAreaSkill = new(
            SkillId: 8_102,
            Target: 1,
            AffectObject: 8,
            Distance: 0f,
            AreaRadius: 8f,
            ManaCost: 13,
            Property: 1,
            Power1: 0.25m,
            Power2: 40m);

    private static void CheckEcsHostileSkillResolution()
    {
        CheckCombatEventIdentityDomains();
        CheckResolvedSingleSkill(CombatHitOutcome.Normal);
        CheckResolvedSingleSkill(CombatHitOutcome.Critical);
        CheckResolvedSingleSkill(CombatHitOutcome.Miss);
        CheckResolvedAreaSkill();
    }

    private static void CheckCombatEventIdentityDomains()
    {
        const int attackerId = 7;
        const uint targetId = 7_801;
        const uint spawnGeneration = 3;
        const ulong healthRevision = 9;
        const ulong admittedRevision = 11;
        var basic = CombatEventIdentity.ForPlayerMonsterBasicAttack(
            attackerId,
            targetId,
            spawnGeneration,
            healthRevision,
            admittedRevision);
        var skill = CombatEventIdentity.ForPlayerMonsterSkill(
            attackerId,
            targetId,
            spawnGeneration,
            healthRevision,
            admittedRevision,
            ResolutionPhysicalSkill.SkillId,
            targetOrder: 0);
        var otherSkill = CombatEventIdentity.ForPlayerMonsterSkill(
            attackerId,
            targetId,
            spawnGeneration,
            healthRevision,
            admittedRevision,
            ResolutionMagicAreaSkill.SkillId,
            targetOrder: 0);
        var otherOrder = CombatEventIdentity.ForPlayerMonsterSkill(
            attackerId,
            targetId,
            spawnGeneration,
            healthRevision,
            admittedRevision,
            ResolutionMagicAreaSkill.SkillId,
            targetOrder: 1);
        var pvpBasic = CombatEventIdentity.ForPlayerBasicAttack(
            attackerId,
            (int)targetId,
            (long)spawnGeneration,
            (long)healthRevision,
            (long)admittedRevision);
        Check.Equal(5, new HashSet<ulong>
            {
                basic,
                skill,
                otherSkill,
                otherOrder,
                pvpBasic
            }.Count,
            "combat event domains skill IDs and target order cannot collide");
        Check.Equal(skill,
            CombatEventIdentity.ForPlayerMonsterSkill(
                attackerId,
                targetId,
                spawnGeneration,
                healthRevision,
                admittedRevision,
                ResolutionPhysicalSkill.SkillId,
                targetOrder: 0),
            "replaying one admitted skill returns the same event identity");
    }

    private static void CheckResolvedSingleSkill(
        CombatHitOutcome outcome)
    {
        var offense = CreateResolutionOffense();
        var target = CreateSkillResolutionTarget(7_801, x: 1f);
        var admittedRevision = FindSkillRevision(
            offense,
            target,
            ResolutionPhysicalSkill,
            targetOrder: 0,
            resolution => resolution.Outcome == outcome);
        var fixture = CreateFixture();
        fixture.World.Set(fixture.Player, offense);
        ref var resources = ref fixture.World
            .Get<PlayerCombatResourceComponent>(fixture.Player);
        resources.CombatRevision = admittedRevision - 1UL;
        PlayerCombatEcsBoundary.HydrateTarget(fixture.World, target);
        QueueResolutionSkill(
            fixture,
            PlayerCombatIntentKind.SingleTargetSkill,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            ResolutionPhysicalSkill);
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var resolved = Events<PlayerCombatTargetResolvedEvent>(fixture)
            .Single();
        var expected = ResolveExpectedSkill(
            offense,
            target,
            ResolutionPhysicalSkill,
            admittedRevision,
            targetOrder: 0);
        Check.Equal(expected, resolved.Resolution,
            $"ECS {outcome} skill matches legacy/shared resolution");
        Check.Equal(1, resolved.TargetCount,
            $"ECS {outcome} single skill reports one selected target");
        Check.True(
            resolved.Resolution.Channel == CombatDamageChannel.Physical,
            $"ECS {outcome} single skill uses physical target defenses");
        Check.Equal(
            ResolutionPhysicalSkill.ManaCost,
            100 - fixture.World
                .Get<PlayerCombatResourceComponent>(fixture.Player)
                .CurrentMp,
            $"ECS {outcome} single skill reserves mana once");

        var damageIntents =
            Events<PlayerCombatDamageIntentEvent>(fixture);
        if (outcome == CombatHitOutcome.Miss)
        {
            Check.Equal(0, damageIntents.Length,
                "ECS skill miss emits no health mutation");
            Check.True(
                !fixture.World.Has<PlayerCombatReservationComponent>(
                    fixture.Player),
                "ECS skill miss closes without a mutation outcome");
            var completed = Events<
                PlayerCombatReservationCompletedEvent>(fixture).Single();
            Check.Equal(1, completed.AcceptedTargetCount,
                "ECS skill miss is an admitted cast, not a rejection");
            Check.True(!completed.ResourcesRefunded,
                "ECS skill miss keeps its admitted mana cost");
            return;
        }

        var damage = damageIntents.Single();
        Check.Equal(expected.Damage, damage.RequestedDamage,
            $"ECS {outcome} skill mutation uses resolved damage");
        Check.True(
            fixture.World.Has<PlayerCombatReservationComponent>(
                fixture.Player),
            $"ECS {outcome} skill awaits guarded monster mutation");
    }

    private static void CheckResolvedAreaSkill()
    {
        var offense = CreateResolutionOffense();
        var targets = new[]
        {
            CreateSkillResolutionTarget(7_903, x: 3f),
            CreateSkillResolutionTarget(7_901, x: 1f),
            CreateSkillResolutionTarget(7_902, x: 2f)
        };
        var ordered = targets.OrderBy(static target => target.ObjectId)
            .ToArray();
        var admittedRevision = FindMixedAreaRevision(
            offense,
            ordered,
            ResolutionMagicAreaSkill);
        var fixture = CreateFixture();
        fixture.World.Set(fixture.Player, offense);
        ref var resources = ref fixture.World
            .Get<PlayerCombatResourceComponent>(fixture.Player);
        resources.CombatRevision = admittedRevision - 1UL;
        foreach (var target in targets)
        {
            PlayerCombatEcsBoundary.HydrateTarget(fixture.World, target);
        }

        QueueResolutionSkill(
            fixture,
            PlayerCombatIntentKind.AreaSkill,
            uint.MaxValue,
            expectedSpawnGeneration: 0,
            expectedHealthRevision: 0,
            ResolutionMagicAreaSkill);
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var resolved = Events<PlayerCombatTargetResolvedEvent>(fixture);
        Check.Equal(ordered.Length, resolved.Length,
            "ECS area resolves every selected target");
        var expectedHits = new List<(int Order, uint ObjectId, uint Damage)>();
        var missCount = 0;
        for (var targetOrder = 0;
             targetOrder < ordered.Length;
             targetOrder++)
        {
            var target = ordered[targetOrder];
            var expected = ResolveExpectedSkill(
                offense,
                target,
                ResolutionMagicAreaSkill,
                admittedRevision,
                targetOrder);
            Check.Equal(target.ObjectId, resolved[targetOrder].TargetObjectId,
                "ECS area resolution retains object-ID order");
            Check.Equal(ordered.Length, resolved[targetOrder].TargetCount,
                "ECS area resolution carries total selected targets");
            Check.Equal(expected, resolved[targetOrder].Resolution,
                "ECS area target matches legacy/shared resolution");
            Check.True(
                expected.Channel == CombatDamageChannel.Magic,
                "ECS area target uses magic target defenses");
            if (expected.Hit && expected.Damage > 0)
            {
                expectedHits.Add((
                    targetOrder,
                    target.ObjectId,
                    expected.Damage));
            }
            else
            {
                missCount++;
            }
        }

        Check.True(missCount > 0 && expectedHits.Count > 0,
            "deterministic area fixture covers hits and misses together");
        var damageIntents = Events<PlayerCombatDamageIntentEvent>(fixture);
        Check.Equal(expectedHits.Count, damageIntents.Length,
            "ECS area emits mutation intents only for resolved hits");
        for (var index = 0; index < expectedHits.Count; index++)
        {
            Check.Equal(expectedHits[index].Order,
                damageIntents[index].TargetOrder,
                "ECS area hit retains its selected-target order");
            Check.Equal(expectedHits[index].ObjectId,
                damageIntents[index].TargetObjectId,
                "ECS area hit retains its target identity");
            Check.Equal(expectedHits[index].Damage,
                damageIntents[index].RequestedDamage,
                "ECS area hit mutation uses resolved damage");
            Check.Equal(ordered.Length, damageIntents[index].TargetCount,
                "ECS area hit mutation carries total target count");
        }

        var reservation = fixture.World
            .Get<PlayerCombatReservationComponent>(fixture.Player);
        Check.Equal(missCount, reservation.AcceptedTargetCount,
            "ECS area reservation accounts for misses immediately");
        Check.Equal(expectedHits.Count, reservation.Targets.Length,
            "ECS area reservation waits only for health mutations");
    }

    private static ulong FindMixedAreaRevision(
        in PlayerCombatOffenseComponent offense,
        IReadOnlyList<PlayerCombatTargetComponent> targets,
        in PlayerCombatSkillSnapshot skill)
    {
        for (ulong revision = 1; revision <= 10_000; revision++)
        {
            var hitCount = 0;
            for (var targetOrder = 0;
                 targetOrder < targets.Count;
                 targetOrder++)
            {
                if (ResolveExpectedSkill(
                        offense,
                        targets[targetOrder],
                        skill,
                        revision,
                        targetOrder).Hit)
                {
                    hitCount++;
                }
            }

            if (hitCount > 0 && hitCount < targets.Count)
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "No deterministic mixed-outcome area fixture was found.");
    }

    private static ulong FindSkillRevision(
        in PlayerCombatOffenseComponent offense,
        in PlayerCombatTargetComponent target,
        in PlayerCombatSkillSnapshot skill,
        int targetOrder,
        Func<CombatResolution, bool> predicate)
    {
        for (ulong revision = 1; revision <= 10_000; revision++)
        {
            var resolution = ResolveExpectedSkill(
                offense,
                target,
                skill,
                revision,
                targetOrder);
            if (predicate(resolution))
            {
                return revision;
            }
        }

        throw new InvalidOperationException(
            "No deterministic skill outcome fixture was found.");
    }

    private static CombatResolution ResolveExpectedSkill(
        in PlayerCombatOffenseComponent offense,
        in PlayerCombatTargetComponent target,
        in PlayerCombatSkillSnapshot skill,
        ulong admittedRevision,
        int targetOrder)
    {
        var attacker = CombatCharacterStatsAdapter.FromOffense(offense);
        var eventId = CombatEventIdentity.ForPlayerMonsterSkill(
            attackerCharacterId: 7,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            admittedRevision,
            skill.SkillId,
            targetOrder);
        var ecs = PlayerCombatRules.ResolveSkillDamage(
            attacker,
            ToTargetStats(target),
            skill,
            eventId,
            targetOrder);
        var legacy = SkillCombatResolver.ResolveDamage(
            CreateAuthoredCharacter(attacker),
            ToLegacy(skill),
            ToTargetStats(target),
            eventId,
            targetOrder);
        Check.Equal(legacy, ecs,
            "legacy and ECS hostile skill hooks retain exact parity");
        return ecs;
    }

    private static PlayerCombatTargetComponent
        CreateSkillResolutionTarget(uint objectId, float x) =>
        CreateResolutionTarget() with
        {
            ObjectId = objectId,
            X = x,
            CurrentHealth = 20_000
        };

    private static void QueueResolutionSkill(
        Fixture fixture,
        PlayerCombatIntentKind kind,
        uint targetObjectId,
        uint expectedSpawnGeneration,
        ulong expectedHealthRevision,
        in PlayerCombatSkillSnapshot skill)
    {
        PlayerCombatEcsBoundary.QueueIntent(
            fixture.World,
            fixture.Player,
            new PlayerCombatIntentComponent(
                IntentId: 8_100,
                kind,
                Start,
                targetObjectId,
                expectedSpawnGeneration,
                expectedHealthRevision,
                ReportedAttackerX: 0f,
                ReportedAttackerZ: 0f,
                HasReportedTargetPosition: false,
                ReportedTargetX: float.NaN,
                ReportedTargetZ: float.NaN,
                skill));
    }
}
