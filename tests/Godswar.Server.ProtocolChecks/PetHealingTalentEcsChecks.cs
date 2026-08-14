using Godswar.Server.Ecs;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class PetHealingTalentEcsChecks
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 11, 1, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        CheckVersionedPolicy();
        CheckThresholdBoundary();
        CheckCooldownAndReplayGuards();
        CheckEligibilityAndLethalGuards();
        CheckBoundedCooldownLedger();
        return Task.CompletedTask;
    }

    private static void CheckVersionedPolicy()
    {
        Check.Equal(2, PetHealingTalentPolicy.Version,
            "pet Healing policy version");
        Check.Equal(4_000,
            PetHealingTalentPolicy.TriggerThresholdBasisPoints,
            "pet Healing threshold basis points");
        Check.Equal(TimeSpan.FromSeconds(180),
            PetHealingTalentPolicy.Cooldown,
            "pet Healing cooldown");
        var amount = PetHealingTalentPolicy.ResolveAmount(
            aptitude: 16,
            petLevel: 120,
            currentHealth: 40_000,
            maximumHealth: 100_000);
        Check.Equal(25_000, amount.Resolved,
            "level-120 Transcendent pet heals 25 percent Max HP");
        Check.Equal(25_000, amount.Applied,
            "pet Healing applies its full quality-scaled amount");
        Check.Equal(12_000,
            PetHealingTalentPolicy.ResolveAmount(
                aptitude: 10,
                petLevel: 120,
                currentHealth: 40_000,
                maximumHealth: 100_000).Resolved,
            "level-120 Smart pet heals 12 percent Max HP");
        Check.Equal(12_500,
            PetHealingTalentPolicy.ResolveAmount(
                aptitude: 16,
                petLevel: 1,
                currentHealth: 40_000,
                maximumHealth: 100_000).Resolved,
            "level-one pet starts at half quality effectiveness");
        Check.Equal(10_000,
            PetHealingTalentPolicy.ResolveAmount(
                aptitude: 16,
                petLevel: 120,
                currentHealth: 90_000,
                maximumHealth: 100_000).Applied,
            "pet Healing remains capped by missing HP");
    }

    private static void CheckThresholdBoundary()
    {
        var above = CreateFixture(currentHp: 50, maximumHp: 100);
        Queue(above, eventId: 1, damage: 9, resolvedAt: Start);
        above.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(0, Heals(above).Length,
            "41 percent HP does not trigger pet Healing");
        Check.Equal(41,
            above.World.Get<PlayerVitalsComponent>(
                above.Player.Entity).CurrentHp,
            "above-threshold damage remains authoritative");

        var exact = CreateFixture(currentHp: 50, maximumHp: 100);
        Queue(exact, eventId: 1, damage: 10, resolvedAt: Start);
        exact.Scheduler.RunTick(TimeSpan.Zero);
        var exactHeal = Heals(exact).Single();
        Check.Equal(40, exactHeal.BeforeHealth,
            "40 percent boundary triggers pet Healing");
        Check.Equal(65, exactHeal.AfterHealth,
            "boundary heal applies the quality-scaled Max-HP amount");
        Check.Equal(2L, exactHeal.AfterVitalsRevision,
            "damage and Healing each advance vitals revision");

        var below = CreateFixture(currentHp: 50, maximumHp: 100);
        Queue(below, eventId: 1, damage: 11, resolvedAt: Start);
        below.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(1, Heals(below).Length,
            "below 40 percent HP triggers pet Healing");
    }

    private static void CheckCooldownAndReplayGuards()
    {
        var fixture = CreateFixture(
            currentHp: 500,
            maximumHp: 1_000);
        Queue(fixture, eventId: 10, damage: 100, resolvedAt: Start);
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        var first = Heals(fixture).Single();
        Check.Equal(Start.AddSeconds(180), first.CooldownReadyAt,
            "successful Healing owns an exact 180-second cooldown");
        Check.Equal(650, first.AfterHealth,
            "first Healing applies its quality and level-scaled amount");

        Queue(
            fixture,
            eventId: 10,
            damage: 1,
            resolvedAt: Start.AddSeconds(1));
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(0, Heals(fixture).Length,
            "duplicate attack cannot trigger Healing twice");
        Check.Equal(650,
            fixture.World.Get<PlayerVitalsComponent>(
                fixture.Player.Entity).CurrentHp,
            "duplicate attack cannot mutate healed HP");

        Queue(
            fixture,
            eventId: 11,
            damage: 250,
            resolvedAt: Start.AddSeconds(179));
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(0, Heals(fixture).Length,
            "Healing remains unavailable before 180 seconds");
        Check.Equal(400,
            fixture.World.Get<PlayerVitalsComponent>(
                fixture.Player.Entity).CurrentHp,
            "cooldown suppresses Healing but not accepted damage");

        Queue(
            fixture,
            eventId: 12,
            damage: 1,
            resolvedAt: Start.AddSeconds(180));
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(1, Heals(fixture).Length,
            "Healing is available exactly at 180 seconds");
    }

    private static void CheckEligibilityAndLethalGuards()
    {
        var noTalent = CreateFixture(
            currentHp: 50,
            maximumHp: 100,
            talentMask: 16);
        Queue(noTalent, eventId: 1, damage: 10, resolvedAt: Start);
        noTalent.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(0, Heals(noTalent).Length,
            "pet without Healing talent cannot heal");

        var absent = CreateFixture(
            currentHp: 50,
            maximumHp: 100,
            installActivePet: false);
        Queue(absent, eventId: 1, damage: 10, resolvedAt: Start);
        absent.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(0, Heals(absent).Length,
            "unsummoned pet projection cannot heal");

        var lethal = CreateFixture(currentHp: 10, maximumHp: 100);
        Queue(lethal, eventId: 1, damage: 10, resolvedAt: Start);
        lethal.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(0, Heals(lethal).Length,
            "lethal damage cannot trigger pet Healing");
        Check.Equal(1,
            lethal.Scheduler.Events
                .Read<MonsterPlayerDeathDecisionEvent>().Length,
            "lethal hit remains a death decision");
    }

    private static void CheckBoundedCooldownLedger()
    {
        var store = new ProcessPetHealingCooldownStore(capacity: 1);
        Check.True(store.TryClaim(
                new PetHealingCooldownKey(1, 10),
                Start,
                PetHealingTalentPolicy.Cooldown,
                out _),
            "bounded ledger accepts its first owner-pet key");
        Check.True(!store.TryClaim(
                new PetHealingCooldownKey(2, 20),
                Start.AddSeconds(1),
                PetHealingTalentPolicy.Cooldown,
                out _),
            "bounded ledger fails closed at capacity");
        Check.Equal(1, store.Count,
            "bounded ledger never exceeds capacity");
        Check.True(store.TryClaim(
                new PetHealingCooldownKey(2, 20),
                Start.AddSeconds(180),
                PetHealingTalentPolicy.Cooldown,
                out _),
            "bounded ledger reclaims expired entries");
        Check.Equal(1, store.Count,
            "expired-ledger reclamation remains bounded");

        var concurrent = new ProcessPetHealingCooldownStore();
        var claims = 0;
        Parallel.For(
            fromInclusive: 0,
            toExclusive: 64,
            iteration =>
            {
                if (concurrent.TryClaim(
                        new PetHealingCooldownKey(3, 30),
                        Start,
                        PetHealingTalentPolicy.Cooldown,
                        out _))
                {
                    Interlocked.Increment(ref claims);
                }
            });
        Check.Equal(1, claims,
            "concurrent hits can claim one Healing cooldown only once");
    }

    private static Fixture CreateFixture(
        int currentHp,
        int maximumHp,
        short talentMask = 8,
        short aptitude = 16,
        short petLevel = 120,
        bool installActivePet = true)
    {
        var world = new EcsWorld();
        var player = MonsterPlayerDamageEcsBoundary.HydratePlayer(
            world,
            new MonsterPlayerDamageHydrationSnapshot(
                CharacterId: 17,
                AccountId: 4,
                PlayerObjectId: 0x1448,
                currentHp,
                maximumHp,
                CurrentMp: 40,
                MaximumMp: 40,
                VitalsRevision: 0,
                LifeRevision: 0));
        MonsterPlayerDamageEcsBoundary.SynchronizePetHealingTalent(
            world,
            player,
            installActivePet
                ? new PetHealingTalentHydrationSnapshot(
                    PetId: 70,
                    Level: petLevel,
                    Aptitude: aptitude,
                    TalentMask: talentMask,
                    IsCarried: true,
                    IsSummoned: true)
                : null);
        var cooldowns = new ProcessPetHealingCooldownStore();
        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new MonsterPlayerDamageSystem());
        scheduler.AddSystem(new PetHealingTalentSystem(cooldowns));
        return new Fixture(world, scheduler, player);
    }

    private static void Queue(
        Fixture fixture,
        ulong eventId,
        uint damage,
        DateTimeOffset resolvedAt)
    {
        var vitals = fixture.World.Get<PlayerVitalsComponent>(
            fixture.Player.Entity);
        MonsterPlayerDamageEcsBoundary.QueueDamage(
            fixture.World,
            fixture.Player,
            new MonsterPlayerDamageIntentComponent(
                eventId,
                MonsterObjectId: 9_001,
                MonsterSpawnGeneration: 2,
                ExpectedCharacterId: 17,
                ExpectedPlayerObjectId: 0x1448,
                ExpectedLifeRevision: 0,
                ExpectedVitalsRevision: vitals.Revision,
                ResolvedDamage: damage,
                ResolvedAt: resolvedAt));
    }

    private static PetHealingAppliedEvent[] Heals(Fixture fixture) =>
        fixture.Scheduler.Events
            .Read<PetHealingAppliedEvent>().ToArray();

    private readonly record struct Fixture(
        EcsWorld World,
        EcsSystemScheduler Scheduler,
        MonsterPlayerDamageEntity Player);
}
