using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game;

internal sealed record PlayerStatusEcsDecision(
    PlayerStatusSnapshot Snapshot,
    ImmutableArray<ActiveRuntimeStatus> ActiveRuntimeStatuses,
    ImmutableArray<PlayerRuntimeStatusExpiredEvent> ExpiredStatuses,
    bool CompositionChanged,
    DateTimeOffset CurrentAt);

/// <summary>
/// Owns status expiry and complete-snapshot composition for one player.
/// Sockets, packets, persistence, and mutable session objects stay outside.
/// </summary>
internal sealed class PlayerStatusEcsAdapter
{
    private static readonly PlayerStatusSnapshot NeutralStatus = new(
        [],
        ClientStatusAggregate.Empty,
        "player-status-ecs-neutral");

    private readonly object _gate = new();
    private EcsWorld? _world;
    private EcsSystemScheduler? _scheduler;
    private EntityId _entity;
    private int _characterId;
    private uint _objectId;
    private PlayerStatusEcsDecision? _lastDecision;

    public PlayerStatusEcsDecision? Snapshot()
    {
        lock (_gate)
        {
            return _lastDecision;
        }
    }

    public PlayerStatusEcsDecision Evaluate(
        GameCharacter character,
        uint objectId,
        ExperienceBoostState experienceBoosts,
        IEnumerable<ActiveRuntimeStatus> runtimeStatuses,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(experienceBoosts);
        ArgumentNullException.ThrowIfNull(runtimeStatuses);

        var experience = ImmutableArray.CreateRange(
            experienceBoosts.ActiveBoosts);
        var runtime = ImmutableArray.CreateRange(runtimeStatuses);

        lock (_gate)
        {
            EnsureAttached(
                character,
                objectId,
                observedAt,
                experience,
                runtime);
            var world = _world!;
            var scheduler = _scheduler!;
            world.Set(
                _entity,
                new PlayerStatusSourceComponent(experience, runtime));
            world.Set(
                _entity,
                new PlayerRuntimeTimeSourceComponent(observedAt));
            scheduler.RunTick(TimeSpan.Zero);

            var output = world.Get<PlayerComposedStatusComponent>(_entity);
            var source = world.Get<PlayerStatusSourceComponent>(_entity);
            var clock = world.Get<PlayerRuntimeClockComponent>(_entity);
            var snapshot = new PlayerStatusSnapshot(
                output.Effects
                    .Select(static effect => new ClientStatusEffect(
                        effect.StatusId,
                        effect.RemainingSeconds))
                    .ToArray(),
                output.Aggregate,
                output.Fingerprint);
            var decision = new PlayerStatusEcsDecision(
                snapshot,
                source.RuntimeStatuses,
                scheduler.Events
                    .Read<PlayerRuntimeStatusExpiredEvent>()
                    .ToArray()
                    .ToImmutableArray(),
                scheduler.Events.Count<
                    PlayerStatusCompositionChangedEvent>() > 0,
                clock.CurrentAt);
            _lastDecision = decision;
            return decision;
        }
    }

    private void EnsureAttached(
        GameCharacter character,
        uint objectId,
        DateTimeOffset observedAt,
        ImmutableArray<ActiveExperienceBoost> experience,
        ImmutableArray<ActiveRuntimeStatus> runtime)
    {
        if (_world is not null &&
            _world.IsAlive(_entity) &&
            _characterId == character.Id &&
            _objectId == objectId)
        {
            return;
        }

        var world = new EcsWorld();
        var entity = GameCharacterEcsHydrator.Hydrate(
            world,
            character,
            objectId,
            worldRevision: 0,
            NeutralStatus);
        PlayerRuntimeEcsHydrator.Attach(
            world,
            entity,
            new PlayerRuntimeEcsSeed(
                observedAt,
                experience,
                runtime,
                ProgressionOnlineStartedAt: null,
                ZodiacOnlineStartedAt: null));

        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new PlayerRuntimeClockSystem());
        scheduler.AddSystem(new PlayerStatusCompositionSystem());
        _world = world;
        _scheduler = scheduler;
        _entity = entity;
        _characterId = character.Id;
        _objectId = objectId;
        _lastDecision = null;
    }
}
