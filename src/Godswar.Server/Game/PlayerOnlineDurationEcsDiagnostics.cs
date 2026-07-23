using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game;

internal readonly record struct PlayerOnlineDurationEcsSnapshot(
    long ProgressionCommittedIntervals,
    long ProgressionElapsedTicks,
    long ZodiacCommittedIntervals,
    long ZodiacElapsedTicks,
    long Discontinuities,
    long StaleObservations);

/// <summary>
/// Mirrors only intervals that the legacy persistence adapter committed.
/// Store writes remain authoritative until their transaction semantics move
/// behind an ECS-compatible durable outbox.
/// </summary>
internal sealed class PlayerOnlineDurationEcsDiagnostics
{
    private readonly object _gate = new();
    private CommittedClock? _progression;
    private CommittedClock? _zodiac;
    private long _discontinuities;
    private long _staleObservations;

    public PlayerOnlineDurationAccountedEvent? ObserveCommitted(
        int accountId,
        int characterId,
        PlayerOnlineDurationTarget target,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil)
    {
        if (onlineUntil <= onlineFrom)
        {
            return null;
        }

        lock (_gate)
        {
            var clock = target switch
            {
                PlayerOnlineDurationTarget.ProgressionBoosts =>
                    _progression,
                PlayerOnlineDurationTarget.Zodiac =>
                    _zodiac,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(target),
                    target,
                    "Online diagnostics require one duration target.")
            };

            if (clock is null ||
                clock.AccountId != accountId ||
                clock.CharacterId != characterId)
            {
                clock = new CommittedClock(
                    accountId,
                    characterId,
                    target,
                    onlineFrom);
            }
            else if (onlineFrom != clock.Watermark)
            {
                if (onlineFrom < clock.Watermark)
                {
                    _staleObservations++;
                    return null;
                }

                _discontinuities++;
                clock = new CommittedClock(
                    accountId,
                    characterId,
                    target,
                    onlineFrom);
            }

            var observed = clock.Observe(onlineUntil);
            if (target ==
                PlayerOnlineDurationTarget.ProgressionBoosts)
            {
                _progression = clock;
            }
            else
            {
                _zodiac = clock;
            }

            return observed;
        }
    }

    public PlayerOnlineDurationEcsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PlayerOnlineDurationEcsSnapshot(
                _progression?.CommittedIntervals ?? 0,
                _progression?.ElapsedTicks ?? 0,
                _zodiac?.CommittedIntervals ?? 0,
                _zodiac?.ElapsedTicks ?? 0,
                _discontinuities,
                _staleObservations);
        }
    }

    private sealed class CommittedClock
    {
        private readonly EcsWorld _world = new();
        private readonly EcsSystemScheduler _scheduler;
        private readonly EntityId _entity;
        private readonly PlayerOnlineDurationTarget _target;

        public CommittedClock(
            int accountId,
            int characterId,
            PlayerOnlineDurationTarget target,
            DateTimeOffset watermark)
        {
            AccountId = accountId;
            CharacterId = characterId;
            Watermark = watermark;
            _target = target;

            _world.RegisterComponent<PlayerIdentityComponent>();
            _world.RegisterComponent<PlayerRuntimeTimeSourceComponent>();
            _world.RegisterComponent<PlayerRuntimeClockComponent>();
            _world.RegisterComponent<PlayerOnlineDurationClocksComponent>();
            _entity = _world.CreateEntity();
            _world.Add(
                _entity,
                new PlayerIdentityComponent(
                    characterId,
                    accountId,
                    ObjectId: 1,
                    Name: string.Empty,
                    CreatedUtc: DateTime.UnixEpoch,
                    WorldRevision: 0));
            _world.Add(
                _entity,
                new PlayerRuntimeTimeSourceComponent(watermark));
            _world.Add(
                _entity,
                new PlayerRuntimeClockComponent(watermark));
            _world.Add(
                _entity,
                new PlayerOnlineDurationClocksComponent(
                    target == PlayerOnlineDurationTarget.ProgressionBoosts
                        ? watermark
                        : null,
                    target == PlayerOnlineDurationTarget.Zodiac
                        ? watermark
                        : null));

            _scheduler = new EcsSystemScheduler(_world);
            _scheduler.AddSystem(new PlayerRuntimeClockSystem());
            _scheduler.AddSystem(new PlayerOnlineDurationSystem());
        }

        public int AccountId { get; }

        public int CharacterId { get; }

        public DateTimeOffset Watermark { get; private set; }

        public long CommittedIntervals { get; private set; }

        public long ElapsedTicks { get; private set; }

        public PlayerOnlineDurationAccountedEvent? Observe(
            DateTimeOffset onlineUntil)
        {
            _world.Set(
                _entity,
                new PlayerRuntimeTimeSourceComponent(onlineUntil));
            _scheduler.RunTick(TimeSpan.Zero);
            var matching = _scheduler.Events
                .Read<PlayerOnlineDurationAccountedEvent>()
                .ToArray()
                .Where(candidate => candidate.Target == _target)
                .ToArray();
            if (matching.Length != 1)
            {
                throw new InvalidOperationException(
                    "A committed online interval must emit exactly one ECS event.");
            }

            var accounted = matching[0];
            Watermark = accounted.OnlineUntil;
            CommittedIntervals = checked(CommittedIntervals + 1);
            ElapsedTicks = checked(ElapsedTicks + accounted.ElapsedTicks);
            return accounted;
        }
    }
}
