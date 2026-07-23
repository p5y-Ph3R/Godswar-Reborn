using Godswar.Server.Ecs;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game;

internal readonly record struct PlayerMovementEcsDecision(
    bool Accepted,
    PlayerMovementRejectionReason RejectionReason,
    ulong IntentSequence,
    ulong ProjectionRevision,
    byte MapId,
    float PreviousX,
    float PreviousZ,
    float TargetX,
    float TargetZ,
    float CurrentX,
    float CurrentZ);

/// <summary>
/// Session-owned boundary for deterministic ECS movement projection. It owns
/// no socket, packet, registry, persistence, or mutable character reference.
/// </summary>
internal sealed class PlayerMovementEcsAdapter
{
    private readonly object _gate = new();
    private EcsWorld? _world;
    private EcsSystemScheduler? _scheduler;
    private EntityId _entity;
    private ulong _nextIntentSequence;
    private PlayerMovementEcsDecision? _lastDecision;

    public PlayerMovementEcsDecision? Snapshot()
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
            _nextIntentSequence = 0;
            _lastDecision = null;
        }
    }

    public PlayerMovementEcsDecision Evaluate(
        GameCharacter character,
        int sessionAccountId,
        uint expectedSourceObjectId,
        uint? verifiedSourceObjectId,
        float targetX,
        float targetZ)
    {
        ArgumentNullException.ThrowIfNull(character);

        lock (_gate)
        {
            EnsureAttached(
                character,
                sessionAccountId,
                expectedSourceObjectId);
            var world = _world!;
            var scheduler = _scheduler!;
            var sequence = checked(_nextIntentSequence + 1);
            _nextIntentSequence = sequence;
            world.Add(
                _entity,
                new PlayerMovementIntentComponent(
                    sequence,
                    verifiedSourceObjectId,
                    sessionAccountId,
                    character.AccountId,
                    character.Id,
                    character.CurrentMap,
                    targetX,
                    targetZ));
            scheduler.RunTick(TimeSpan.Zero);

            var accepted = scheduler.Events
                .Read<PlayerMovementProjectedEvent>();
            var rejected = scheduler.Events
                .Read<PlayerMovementRejectedEvent>();
            if (accepted.Length + rejected.Length != 1)
            {
                throw new InvalidOperationException(
                    "One movement intent must emit exactly one decision.");
            }

            var decision = accepted.Length == 1
                ? FromAccepted(accepted[0])
                : FromRejected(rejected[0]);
            _lastDecision = decision;
            return decision;
        }
    }

    private void EnsureAttached(
        GameCharacter character,
        int sessionAccountId,
        uint expectedSourceObjectId)
    {
        if (_world is not null && _world.IsAlive(_entity))
        {
            return;
        }

        var world = new EcsWorld();
        world.RegisterComponent<PlayerMovementIdentityComponent>();
        world.RegisterComponent<PlayerMovementTransformComponent>();
        world.RegisterComponent<PlayerMovementIntentComponent>();
        var entity = world.CreateEntity();
        world.Add(
            entity,
            new PlayerMovementIdentityComponent(
                sessionAccountId,
                character.Id,
                expectedSourceObjectId));
        world.Add(
            entity,
            new PlayerMovementTransformComponent(
                character.CurrentMap,
                character.PositionX,
                character.PositionZ));
        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(
            new PlayerMovementProjectionSystem());

        _world = world;
        _scheduler = scheduler;
        _entity = entity;
        _nextIntentSequence = 0;
        _lastDecision = null;
    }

    private static PlayerMovementEcsDecision FromAccepted(
        in PlayerMovementProjectedEvent projected) =>
        new(
            Accepted: true,
            PlayerMovementRejectionReason.None,
            projected.IntentSequence,
            projected.ProjectionRevision,
            projected.MapId,
            projected.PreviousX,
            projected.PreviousZ,
            projected.TargetX,
            projected.TargetZ,
            projected.CurrentX,
            projected.CurrentZ);

    private static PlayerMovementEcsDecision FromRejected(
        in PlayerMovementRejectedEvent rejected) =>
        new(
            Accepted: false,
            rejected.Reason,
            rejected.IntentSequence,
            rejected.ProjectionRevision,
            rejected.MapId,
            rejected.CurrentX,
            rejected.CurrentZ,
            rejected.TargetX,
            rejected.TargetZ,
            rejected.CurrentX,
            rejected.CurrentZ);
}
