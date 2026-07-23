using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerRuntimeEcsShadowChecks
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        CheckRecoveryParity();
        CheckStatusExpiryAndMountParity();
        CheckOnlineDurationAccounting();
        return Task.CompletedTask;
    }

    private static void CheckRecoveryParity()
    {
        var legacy = CreateCharacter();
        var ecsCharacter = CreateCharacter();
        var fixture = CreateFixture(
            ecsCharacter,
            Start,
            ImmutableArray<ActiveExperienceBoost>.Empty,
            ImmutableArray<ActiveRuntimeStatus>.Empty);
        var recovery = fixture.World
            .Get<PlayerRecoverySourceComponent>(fixture.Entity);

        Check.Equal(
            PlayerRecoveryCatalog.GetTotalHp(legacy),
            recovery.HpPerPulse,
            "ECS HP recovery source matches the legacy catalog");
        Check.Equal(
            PlayerRecoveryCatalog.GetTotalMp(legacy),
            recovery.MpPerPulse,
            "ECS MP recovery source matches the legacy catalog");
        Check.Equal(
            GameSessionRegistry.PlayerRecoveryInterval,
            PlayerRecoverySimulationSystem.RecoveryInterval,
            "ECS recovery uses the live six-second interval");

        Observe(fixture, Start.AddSeconds(5));
        Check.Equal(
            0,
            Events<PlayerVitalsRecoveredEvent>(fixture).Length,
            "recovery does not pulse early");

        Observe(fixture, Start.AddSeconds(6));
        Check.True(
            PlayerRecoveryCatalog.TryApply(legacy),
            "legacy recovery applies at the first due pulse");
        AssertVitalsEqual(legacy, fixture, "first recovery");
        var first = Events<PlayerVitalsRecoveredEvent>(fixture).Single();
        Check.Equal(1L, first.VitalsRevision, "ECS recovery revision");
        Check.Equal(1_000, first.PreviousHp, "ECS recovery previous HP");
        Check.Equal(1_076, first.CurrentHp, "ECS recovery current HP");
        Check.Equal(9, first.PreviousMp, "ECS recovery previous MP");
        Check.Equal(53, first.CurrentMp, "ECS recovery current MP");

        legacy.CurrentHp = 1_499;
        legacy.CurrentMp = 176;
        ref var vitals = ref fixture.World
            .Get<PlayerVitalsComponent>(fixture.Entity);
        vitals.CurrentHp = 1_499;
        vitals.CurrentMp = 176;
        Observe(fixture, Start.AddSeconds(12));
        Check.True(
            PlayerRecoveryCatalog.TryApply(legacy),
            "legacy near-full recovery applies");
        AssertVitalsEqual(legacy, fixture, "clamped near-full recovery");
        Check.Equal(
            1,
            Events<PlayerVitalsRecoveredEvent>(fixture).Length,
            "near-full ECS recovery emits one update");

        Observe(fixture, Start.AddSeconds(18));
        Check.True(
            !PlayerRecoveryCatalog.TryApply(legacy),
            "legacy full vitals do not change");
        Check.Equal(
            0,
            Events<PlayerVitalsRecoveredEvent>(fixture).Length,
            "full ECS vitals do not emit an update");
        Check.Equal(
            3L,
            fixture.World
                .Get<PlayerRecoveryTimerComponent>(fixture.Entity)
                .PulsesObserved,
            "full-vitals pulse still advances the cadence");

        Observe(fixture, Start.AddSeconds(18));
        Check.Equal(
            3L,
            fixture.World
                .Get<PlayerRecoveryTimerComponent>(fixture.Entity)
                .PulsesObserved,
            "a repeated timestamp cannot double-pulse recovery");

        legacy.CurrentHp = 0;
        legacy.CurrentMp = 1;
        vitals.CurrentHp = 0;
        vitals.CurrentMp = 1;
        Observe(fixture, Start.AddSeconds(24));
        Check.True(
            !PlayerRecoveryCatalog.TryApply(legacy),
            "legacy dead player does not recover");
        AssertVitalsEqual(legacy, fixture, "dead-player recovery");
        Check.Equal(
            0,
            Events<PlayerVitalsRecoveredEvent>(fixture).Length,
            "dead ECS player emits no recovery event");

        legacy.CurrentHp = 1_000;
        legacy.CurrentMp = 9;
        vitals.CurrentHp = 1_000;
        vitals.CurrentMp = 9;
        Observe(fixture, Start.AddSeconds(60));
        Check.True(
            PlayerRecoveryCatalog.TryApply(legacy),
            "legacy delayed poll applies one pulse");
        AssertVitalsEqual(legacy, fixture, "delayed single-pulse recovery");
        Check.Equal(
            Start.AddSeconds(66),
            fixture.World
                .Get<PlayerRecoveryTimerComponent>(fixture.Entity)
                .NextPulseAt,
            "delayed ECS recovery reschedules from observation time");
    }

    private static void CheckStatusExpiryAndMountParity()
    {
        var experience = ImmutableArray.Create(
            new ActiveExperienceBoost(
                ExperienceStatusIds.MaxExperiencePotion,
                ExperienceBoostKinds.Consumable,
                3_000,
                2,
                Start.AddSeconds(30),
                "ecs-shadow"));
        var mount = RuntimeStatus(
            statusId: 1100,
            kind: MountCatalog.RuntimeStatusKind,
            expiresAt: Start.AddSeconds(10),
            revision: 1,
            movementSpeedBonus: 0.35f);
        var holyWard = RuntimeStatus(
            statusId: 160,
            kind: 6,
            expiresAt: Start.AddSeconds(15),
            revision: 2);
        var sacredZeal = RuntimeStatus(
            statusId: 201,
            kind: 7,
            expiresAt: Start.AddSeconds(15),
            revision: 3,
            modifiers: new ClientStatusAggregate(20, 8, 0f));
        var runtime = ImmutableArray.Create(
            sacredZeal,
            mount,
            holyWard);
        var fixture = CreateFixture(
            CreateCharacter(),
            Start,
            experience,
            runtime,
            useNeutralInitialStatus: true);

        fixture.Scheduler.RunTick(TimeSpan.Zero);
        var initialExpected = PlayerStatusComposer.Compose(
            new ExperienceBoostState(experience),
            runtime,
            Start);
        var initialChanged =
            Events<PlayerStatusCompositionChangedEvent>(fixture).Single();
        Check.Equal(
            initialExpected.Fingerprint,
            initialChanged.Fingerprint,
            "ECS initial status fingerprint matches composer");
        Check.True(
            initialChanged.Effects
                .Select(static effect =>
                    (effect.StatusId, effect.RemainingSeconds))
                .SequenceEqual(
                    initialExpected.Effects.Select(static effect =>
                        (effect.StatusId, effect.RemainingSeconds))),
            "ECS initial status effects match composer");
        Check.True(
            initialChanged.Aggregate.IsRiding,
            "active ECS mount status enables riding");
        Check.Equal(
            1.35f,
            initialChanged.Aggregate.MovementSpeedMultiplier,
            "active ECS mount contributes its movement multiplier");

        Observe(fixture, Start.AddSeconds(5));
        Check.Equal(
            0,
            Events<PlayerStatusCompositionChangedEvent>(fixture).Length,
            "remaining time alone does not emit a replacement snapshot");
        var refreshedEffects = fixture.World
            .Get<PlayerComposedStatusComponent>(fixture.Entity)
            .Effects;
        Check.Equal(
            5u,
            refreshedEffects.Single(static effect =>
                effect.StatusId == 1100).RemainingSeconds,
            "status projection refreshes remaining time without an event");

        Observe(fixture, Start.AddSeconds(15));
        var expired =
            Events<PlayerRuntimeStatusExpiredEvent>(fixture);
        Check.Equal(3, expired.Length, "all due runtime statuses expire");
        Check.Equal(1100u, expired[0].StatusId, "earlier mount expiry is first");
        Check.Equal(160u, expired[1].StatusId, "equal-time expiry sorts by status ID");
        Check.Equal(201u, expired[2].StatusId, "equal-time expiry order is stable");

        var unmounted =
            Events<PlayerStatusCompositionChangedEvent>(fixture).Single();
        Check.True(!unmounted.Aggregate.IsRiding, "expired mount disables riding");
        Check.Equal(
            1f,
            unmounted.Aggregate.MovementSpeedMultiplier,
            "expired mount restores normal movement");
        Check.Equal(
            0,
            fixture.World
                .Get<PlayerStatusSourceComponent>(fixture.Entity)
                .RuntimeStatuses
                .Length,
            "expired runtime statuses are removed from ECS source state");

        Observe(fixture, Start.AddSeconds(5));
        Check.Equal(
            Start.AddSeconds(15),
            fixture.World
                .Get<PlayerRuntimeClockComponent>(fixture.Entity)
                .CurrentAt,
            "runtime clock ignores a stale backwards observation");
        Check.Equal(
            0,
            Events<PlayerRuntimeStatusExpiredEvent>(fixture).Length,
            "clock rollback cannot expire a status twice");
        Check.Equal(
            0,
            Events<PlayerStatusCompositionChangedEvent>(fixture).Length,
            "clock rollback cannot resurrect expired statuses");

        var secondMount = RuntimeStatus(
            statusId: 1110,
            kind: MountCatalog.RuntimeStatusKind,
            expiresAt: Start.AddMinutes(5),
            revision: 4,
            movementSpeedBonus: 0.5f);
        fixture.World.Set(
            fixture.Entity,
            new PlayerStatusSourceComponent(
                experience,
                ImmutableArray.Create(secondMount)));
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        var mounted =
            Events<PlayerStatusCompositionChangedEvent>(fixture).Single();
        Check.True(mounted.Aggregate.IsRiding, "mount source can turn riding on");
        Check.Equal(
            1.5f,
            mounted.Aggregate.MovementSpeedMultiplier,
            "new mount source composes its speed");

        fixture.World.Set(
            fixture.Entity,
            new PlayerStatusSourceComponent(
                experience,
                ImmutableArray<ActiveRuntimeStatus>.Empty));
        fixture.Scheduler.RunTick(TimeSpan.Zero);
        var toggledOff =
            Events<PlayerStatusCompositionChangedEvent>(fixture).Single();
        Check.True(!toggledOff.Aggregate.IsRiding, "empty mount source toggles riding off");
        Check.Equal(
            1f,
            toggledOff.Aggregate.MovementSpeedMultiplier,
            "explicit dismount restores the movement multiplier");

        fixture.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(
            0,
            Events<PlayerStatusCompositionChangedEvent>(fixture).Length,
            "unchanged repeated status tick emits no duplicate snapshot");
    }

    private static void CheckOnlineDurationAccounting()
    {
        var fixture = CreateFixture(
            CreateCharacter(),
            Start,
            ImmutableArray<ActiveExperienceBoost>.Empty,
            ImmutableArray<ActiveRuntimeStatus>.Empty,
            progressionStartedAt: Start,
            zodiacStartedAt: Start);

        fixture.Scheduler.RunTick(TimeSpan.Zero);
        Check.Equal(
            0,
            Events<PlayerOnlineDurationAccountedEvent>(fixture).Length,
            "online clocks do not account a zero-length interval");

        Observe(fixture, Start.AddSeconds(90));
        var first = Events<PlayerOnlineDurationAccountedEvent>(fixture);
        Check.Equal(2, first.Length, "progression and Zodiac receive online time");
        Check.True(
            first[0].Target ==
                PlayerOnlineDurationTarget.ProgressionBoosts,
            "progression duration is emitted first");
        Check.True(
            first[1].Target == PlayerOnlineDurationTarget.Zodiac,
            "Zodiac duration is emitted second");
        Check.Equal(
            TimeSpan.FromSeconds(90).Ticks,
            first[0].ElapsedTicks,
            "progression adapter receives exact elapsed ticks");
        Check.Equal(
            TimeSpan.FromSeconds(90).Ticks,
            first[1].ElapsedTicks,
            "Zodiac adapter receives exact elapsed ticks");
        Check.Equal(17, first[0].AccountId, "online event carries account identity");
        Check.Equal(731, first[0].CharacterId, "online event carries character identity");

        Observe(fixture, Start.AddSeconds(90));
        Check.Equal(
            0,
            Events<PlayerOnlineDurationAccountedEvent>(fixture).Length,
            "repeated online checkpoint cannot consume time twice");

        Observe(fixture, Start.AddSeconds(30));
        Check.Equal(
            0,
            Events<PlayerOnlineDurationAccountedEvent>(fixture).Length,
            "backwards checkpoint cannot consume negative online time");
        Observe(fixture, Start.AddSeconds(100));
        var afterRollback =
            Events<PlayerOnlineDurationAccountedEvent>(fixture);
        Check.True(
            afterRollback.All(
                entry => entry.ElapsedTicks ==
                    TimeSpan.FromSeconds(10).Ticks),
            "forward progress after rollback resumes from durable watermark");

        var worldReadyAt = Start.AddMinutes(10);
        var loading = CreateFixture(
            CreateCharacter(),
            worldReadyAt,
            ImmutableArray<ActiveExperienceBoost>.Empty,
            ImmutableArray<ActiveRuntimeStatus>.Empty,
            progressionStartedAt: null,
            zodiacStartedAt: worldReadyAt);
        Observe(loading, worldReadyAt.AddSeconds(60));
        var loadingEvents =
            Events<PlayerOnlineDurationAccountedEvent>(loading);
        Check.Equal(1, loadingEvents.Length, "world loading advances only Zodiac");
        Check.True(
            loadingEvents[0].Target ==
                PlayerOnlineDurationTarget.Zodiac,
            "progression boost clock remains disabled before world-ready");

        ref var clocks = ref loading.World
            .Get<PlayerOnlineDurationClocksComponent>(loading.Entity);
        clocks.ProgressionLastAccountedAt = worldReadyAt.AddSeconds(60);
        Observe(loading, worldReadyAt.AddSeconds(120));
        var readyEvents =
            Events<PlayerOnlineDurationAccountedEvent>(loading);
        Check.Equal(2, readyEvents.Length, "world-ready enables both online clocks");
        Check.Equal(
            TimeSpan.FromSeconds(60).Ticks,
            readyEvents[0].ElapsedTicks,
            "progression countdown starts at world-ready");
        Check.Equal(
            TimeSpan.FromSeconds(120).Ticks,
            readyEvents[1].TotalElapsedTicks,
            "Zodiac retains its earlier world-loading duration");

        var reconnectAt = Start.AddDays(7);
        var reconnect = CreateFixture(
            CreateCharacter(),
            reconnectAt,
            ImmutableArray<ActiveExperienceBoost>.Empty,
            ImmutableArray<ActiveRuntimeStatus>.Empty,
            progressionStartedAt: reconnectAt,
            zodiacStartedAt: reconnectAt);
        Observe(reconnect, reconnectAt.AddSeconds(10));
        Check.True(
            Events<PlayerOnlineDurationAccountedEvent>(reconnect)
                .All(entry =>
                    entry.ElapsedTicks == TimeSpan.FromSeconds(10).Ticks),
            "new session clocks exclude the offline gap");
    }

    private static RuntimeFixture CreateFixture(
        GameCharacter character,
        DateTimeOffset observedAt,
        ImmutableArray<ActiveExperienceBoost> experience,
        ImmutableArray<ActiveRuntimeStatus> runtime,
        DateTimeOffset? progressionStartedAt = null,
        DateTimeOffset? zodiacStartedAt = null,
        bool useNeutralInitialStatus = false)
    {
        var world = new EcsWorld();
        var initialStatus = useNeutralInitialStatus
            ? PlayerStatusComposer.Compose(
                ExperienceBoostState.Empty,
                [],
                observedAt)
            : PlayerStatusComposer.Compose(
                new ExperienceBoostState(experience),
                runtime,
                observedAt);
        var entity = GameCharacterEcsHydrator.Hydrate(
            world,
            character,
            objectId: 0x1448,
            worldRevision: 1,
            initialStatus);
        PlayerRuntimeEcsHydrator.Attach(
            world,
            entity,
            new PlayerRuntimeEcsSeed(
                observedAt,
                experience,
                runtime,
                progressionStartedAt,
                zodiacStartedAt));
        return new RuntimeFixture(
            world,
            entity,
            PlayerRuntimeEcsSchedule.Create(world));
    }

    private static ActiveRuntimeStatus RuntimeStatus(
        uint statusId,
        int kind,
        DateTimeOffset expiresAt,
        long revision,
        ClientStatusAggregate? modifiers = null,
        float movementSpeedBonus = 0f) =>
        new(
            statusId,
            kind,
            Priority: 1,
            Beneficial: true,
            expiresAt,
            modifiers ?? ClientStatusAggregate.Empty,
            revision,
            MovementSpeedBonus: movementSpeedBonus);

    private static void Observe(
        RuntimeFixture fixture,
        DateTimeOffset observedAt)
    {
        fixture.World.Set(
            fixture.Entity,
            new PlayerRuntimeTimeSourceComponent(observedAt));
        fixture.Scheduler.RunTick(TimeSpan.Zero);
    }

    private static T[] Events<T>(RuntimeFixture fixture)
        where T : struct =>
        fixture.Scheduler.Events.Read<T>().ToArray();

    private static void AssertVitalsEqual(
        GameCharacter expected,
        RuntimeFixture actual,
        string description)
    {
        var vitals = actual.World
            .Get<PlayerVitalsComponent>(actual.Entity);
        Check.Equal(
            expected.CurrentHp,
            vitals.CurrentHp,
            $"{description} HP parity");
        Check.Equal(
            expected.CurrentMp,
            vitals.CurrentMp,
            $"{description} MP parity");
        Check.Equal(
            expected.VitalsRevision,
            vitals.Revision,
            $"{description} revision parity");
    }

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = 731,
            AccountId = 17,
            Name = "RuntimeEcsHero",
            CreatedUtc = Start.UtcDateTime,
            Profession = 0,
            Level = 4,
            CurrentHp = 1_000,
            MaxHp = 1_500,
            CurrentMp = 9,
            MaxMp = 177,
            CalculatedStats = new CharacterStats
            {
                HpRecovery = 10,
                MpRecovery = 5
            }
        };

    private readonly record struct RuntimeFixture(
        EcsWorld World,
        EntityId Entity,
        EcsSystemScheduler Scheduler);
}
