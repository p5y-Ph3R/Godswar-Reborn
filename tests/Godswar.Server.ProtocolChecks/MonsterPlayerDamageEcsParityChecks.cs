using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class MonsterPlayerDamageEcsParityChecks
{
    public static Task RunAsync()
    {
        CheckMitigatedNonlethalDamage();
        CheckDuplicateAndStaleEvents();
        CheckIdentityLifeAndVitalsRejections();
        CheckLethalDamage();
        return Task.CompletedTask;
    }

    private static void CheckMitigatedNonlethalDamage()
    {
        var fixture = CreateFixture();
        var character = new GameCharacter
        {
            CurrentHp = 100,
            MaxHp = 100,
            CurrentMp = 40,
            MaxMp = 40,
            CalculatedStats = new CharacterStats()
        };
        var mitigated =
            MonsterCombatResolver.CalculateMonsterPhysicalAttack(
                tier: 1,
                character,
                receivedDamageReduction: 0.10m);
        Check.Equal(
            21u,
            mitigated,
            "incoming damage ECS receives Holy Ward-resolved damage");

        Queue(
            fixture,
            eventId: 10,
            expectedVitalsRevision: 7,
            damage: mitigated);
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var applied = Events<
            MonsterPlayerDamageAppliedEvent>(fixture).Single();
        Check.Equal(
            100,
            applied.BeforeHealth,
            "nonlethal damage captures before HP");
        Check.Equal(
            79,
            applied.AfterHealth,
            "nonlethal damage applies the mitigated scalar");
        Check.Equal(
            21u,
            applied.AppliedDamage,
            "nonlethal damage reports applied damage");
        Check.Equal(
            8L,
            applied.AfterVitalsRevision,
            "nonlethal damage advances vitals revision");
        Check.Equal(
            3L,
            applied.AfterLifeRevision,
            "nonlethal damage preserves life revision");
        Check.True(
            !applied.Killed,
            "nonlethal damage emits no kill decision");
        Check.Equal(
            0,
            Events<MonsterPlayerDeathDecisionEvent>(
                fixture).Length,
            "nonlethal damage emits no death event");

        var vitals = fixture.World.Get<PlayerVitalsComponent>(
            fixture.Player.Entity);
        Check.Equal(
            40,
            vitals.CurrentMp,
            "incoming damage cannot mutate MP");
    }

    private static void CheckDuplicateAndStaleEvents()
    {
        var fixture = CreateFixture();
        Queue(fixture, eventId: 10);
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(
            79,
            fixture.World.Get<PlayerVitalsComponent>(
                fixture.Player.Entity).CurrentHp,
            "first identified event applies once");

        Queue(
            fixture,
            eventId: 10,
            expectedVitalsRevision: 7);
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            fixture,
            MonsterPlayerDamageRejectionReason
                .DuplicateAttackEvent,
            "duplicate attack event");

        Queue(
            fixture,
            eventId: 9,
            expectedVitalsRevision: 8);
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            fixture,
            MonsterPlayerDamageRejectionReason
                .StaleAttackEvent,
            "out-of-order attack event");

        Queue(
            fixture,
            eventId: 11,
            expectedVitalsRevision: 7);
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            fixture,
            MonsterPlayerDamageRejectionReason
                .VitalsRevisionMismatch,
            "stale vitals event");

        Queue(
            fixture,
            eventId: 11,
            expectedVitalsRevision: 8);
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            fixture,
            MonsterPlayerDamageRejectionReason
                .DuplicateAttackEvent,
            "replayed rejected event");
        Check.Equal(
            79,
            fixture.World.Get<PlayerVitalsComponent>(
                fixture.Player.Entity).CurrentHp,
            "duplicate and stale events cannot apply HP twice");
    }

    private static void CheckIdentityLifeAndVitalsRejections()
    {
        var identity = CreateFixture();
        Queue(
            identity,
            eventId: 1,
            expectedCharacterId: 99);
        identity.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            identity,
            MonsterPlayerDamageRejectionReason
                .IdentityMismatch,
            "mismatched player identity");

        var life = CreateFixture();
        Queue(
            life,
            eventId: 1,
            expectedLifeRevision: 2);
        life.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            life,
            MonsterPlayerDamageRejectionReason
                .LifeRevisionMismatch,
            "stale life");

        var dead = CreateFixture(currentHp: 0);
        Queue(dead, eventId: 1);
        dead.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            dead,
            MonsterPlayerDamageRejectionReason
                .PlayerAlreadyDead,
            "already-dead player");

        var zero = CreateFixture();
        Queue(zero, eventId: 1, damage: 0);
        zero.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            zero,
            MonsterPlayerDamageRejectionReason.ZeroDamage,
            "zero resolved damage");
        var zeroVitals = zero.World.Get<PlayerVitalsComponent>(
            zero.Player.Entity);
        var zeroDamageState = zero.World.Get<
            MonsterPlayerDamageStateComponent>(zero.Player.Entity);
        Check.Equal(
            100,
            zeroVitals.CurrentHp,
            "zero-damage miss does not mutate HP");
        Check.Equal(
            7L,
            zeroVitals.Revision,
            "zero-damage miss does not advance vitals revision");
        Check.Equal(
            1UL,
            zeroDamageState.LastAttackEventId,
            "zero-damage miss consumes its attack event ID");

        Queue(zero, eventId: 1, damage: 21);
        zero.Scheduler.RunTick(TimeSpan.Zero);
        AssertRejected(
            zero,
            MonsterPlayerDamageRejectionReason.DuplicateAttackEvent,
            "replayed zero-damage miss event");
        Check.Equal(
            100,
            zero.World.Get<PlayerVitalsComponent>(
                zero.Player.Entity).CurrentHp,
            "replayed miss event cannot later apply HP damage");
    }

    private static void CheckLethalDamage()
    {
        var fixture = CreateFixture(
            currentHp: 15,
            vitalsRevision: 2,
            lifeRevision: 4);
        Queue(
            fixture,
            eventId: 1,
            expectedVitalsRevision: 2,
            expectedLifeRevision: 4,
            damage: 24);
        fixture.Scheduler.RunTick(TimeSpan.Zero);

        var applied = Events<
            MonsterPlayerDamageAppliedEvent>(fixture).Single();
        var death = Events<
            MonsterPlayerDeathDecisionEvent>(fixture).Single();
        Check.True(
            applied.Killed,
            "lethal damage is classified by ECS");
        Check.Equal(
            24u,
            applied.RequestedDamage,
            "lethal decision retains protocol damage");
        Check.Equal(
            15u,
            applied.AppliedDamage,
            "lethal decision clamps applied HP loss");
        Check.Equal(
            0,
            applied.AfterHealth,
            "lethal damage clamps HP at zero");
        Check.Equal(
            3L,
            applied.AfterVitalsRevision,
            "lethal damage advances vitals once");
        Check.Equal(
            5L,
            death.AfterLifeRevision,
            "death decision advances life revision once");
        Check.Equal(
            applied.DecisionSequence,
            death.DecisionSequence,
            "applied and death events share one decision identity");
    }

    private static Fixture CreateFixture(
        int currentHp = 100,
        long vitalsRevision = 7,
        long lifeRevision = 3)
    {
        var world = new EcsWorld();
        var player =
            MonsterPlayerDamageEcsBoundary.HydratePlayer(
                world,
                new MonsterPlayerDamageHydrationSnapshot(
                    CharacterId: 17,
                    AccountId: 4,
                    PlayerObjectId: 0x1448,
                    currentHp,
                    MaximumHp: 100,
                    CurrentMp: 40,
                    MaximumMp: 40,
                    vitalsRevision,
                    lifeRevision));
        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new MonsterPlayerDamageSystem());
        return new Fixture(world, scheduler, player);
    }

    private static void Queue(
        Fixture fixture,
        ulong eventId,
        int expectedCharacterId = 17,
        long expectedLifeRevision = 3,
        long expectedVitalsRevision = 7,
        uint damage = 21)
    {
        MonsterPlayerDamageEcsBoundary.QueueDamage(
            fixture.World,
            fixture.Player,
            new MonsterPlayerDamageIntentComponent(
                eventId,
                MonsterObjectId: 9_001,
                MonsterSpawnGeneration: 2,
                expectedCharacterId,
                ExpectedPlayerObjectId: 0x1448,
                expectedLifeRevision,
                expectedVitalsRevision,
                damage));
    }

    private static void AssertRejected(
        Fixture fixture,
        MonsterPlayerDamageRejectionReason reason,
        string description)
    {
        var rejected = Events<
            MonsterPlayerDamageRejectedEvent>(fixture).Single();
        Check.True(
            rejected.Reason == reason,
            $"{description} rejection reason");
        Check.Equal(
            0,
            Events<MonsterPlayerDamageAppliedEvent>(
                fixture).Length,
            $"{description} emits no applied event");
        Check.Equal(
            0,
            Events<MonsterPlayerDeathDecisionEvent>(
                fixture).Length,
            $"{description} emits no death event");
    }

    private static T[] Events<T>(Fixture fixture)
        where T : struct =>
        fixture.Scheduler.Events.Read<T>().ToArray();

    private readonly record struct Fixture(
        EcsWorld World,
        EcsSystemScheduler Scheduler,
        MonsterPlayerDamageEntity Player);
}
