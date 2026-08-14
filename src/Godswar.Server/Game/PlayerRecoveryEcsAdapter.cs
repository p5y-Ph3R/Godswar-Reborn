using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game;

internal readonly record struct PlayerRecoveryEcsDecision(
    bool PulseAccepted,
    bool Recovered,
    int CurrentHp,
    int CurrentMp,
    long VitalsRevision,
    DateTimeOffset CurrentAt,
    DateTimeOffset NextPulseAt,
    long PulsesObserved);

/// <summary>
/// Owns the transport-neutral ECS recovery state for one logical player.
/// GameCharacter is copied at this boundary and is never retained by the ECS.
/// </summary>
internal sealed class PlayerRecoveryEcsAdapter
{
    private static readonly PlayerStatusSnapshot NeutralStatus = new(
        [],
        ClientStatusAggregate.Empty,
        "player-recovery-ecs-neutral");

    private readonly object _gate = new();
    private EcsWorld? _world;
    private EcsSystemScheduler? _scheduler;
    private EntityId _entity;
    private int _characterId;
    private uint _objectId;
    private PlayerRecoveryEcsDecision? _lastDecision;

    public PlayerRecoveryEcsDecision? Snapshot()
    {
        lock (_gate)
        {
            return _lastDecision;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _world = null;
            _scheduler = null;
            _entity = EntityId.None;
            _characterId = 0;
            _objectId = 0;
            _lastDecision = null;
        }
    }

    public PlayerRecoveryEcsDecision Evaluate(
        GameCharacter character,
        uint objectId,
        DateTimeOffset recoveryStartedAt,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(character);

        lock (_gate)
        {
            EnsureAttached(
                character,
                objectId,
                recoveryStartedAt);
            var world = _world!;
            var scheduler = _scheduler!;

            lock (character.VitalsSync)
            {
                world.Set(
                    _entity,
                    new PlayerVitalsComponent(
                        character.CurrentHp,
                        character.MaxHp,
                        character.CurrentMp,
                        character.MaxMp,
                        character.VitalsRevision));
                world.Set(
                    _entity,
                    PlayerRecoverySourceComponent.Create(
                        character.Level,
                        character.Profession,
                        character.CalculatedStats?.HpRecovery ?? 0,
                        character.CalculatedStats?.MpRecovery ?? 0));
            }

            world.Set(
                _entity,
                new PlayerRuntimeTimeSourceComponent(observedAt));
            var previousPulses = world
                .Get<PlayerRecoveryTimerComponent>(_entity)
                .PulsesObserved;
            scheduler.RunTick(TimeSpan.Zero);

            var recovered = scheduler.Events
                .Read<PlayerVitalsRecoveredEvent>();
            if (recovered.Length > 1)
            {
                throw new InvalidOperationException(
                    "One player recovery tick emitted more than one pulse.");
            }

            var vitals = world.Get<PlayerVitalsComponent>(_entity);
            var clock = world.Get<PlayerRuntimeClockComponent>(_entity);
            var timer = world.Get<PlayerRecoveryTimerComponent>(_entity);
            var decision = new PlayerRecoveryEcsDecision(
                timer.PulsesObserved > previousPulses,
                recovered.Length == 1,
                vitals.CurrentHp,
                vitals.CurrentMp,
                vitals.Revision,
                clock.CurrentAt,
                timer.NextPulseAt,
                timer.PulsesObserved);
            _lastDecision = decision;
            return decision;
        }
    }

    private void EnsureAttached(
        GameCharacter character,
        uint objectId,
        DateTimeOffset recoveryStartedAt)
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
                recoveryStartedAt,
                ImmutableArray<ActiveExperienceBoost>.Empty,
                ImmutableArray<ActiveRuntimeStatus>.Empty,
                ProgressionOnlineStartedAt: null,
                ZodiacOnlineStartedAt: null));

        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new PlayerRuntimeClockSystem());
        scheduler.AddSystem(new PlayerRecoverySimulationSystem());
        _world = world;
        _scheduler = scheduler;
        _entity = entity;
        _characterId = character.Id;
        _objectId = objectId;
        _lastDecision = null;
    }
}
