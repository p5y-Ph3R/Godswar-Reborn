using Godswar.Server.Ecs;
using Godswar.Server.State;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Components.Players;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal readonly record struct PlayerMonsterDamageEcsRequest(
    ulong AttackEventId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    int ExpectedCharacterId,
    uint ExpectedPlayerObjectId,
    long ExpectedLifeRevision,
    long ExpectedVitalsRevision,
    uint ResolvedDamage);

internal readonly record struct PlayerMonsterDamageEcsDecision(
    bool Applied,
    bool Killed,
    MonsterPlayerDamageRejectionReason RejectionReason,
    ulong DecisionSequence,
    ulong AttackEventId,
    uint MonsterObjectId,
    uint RequestedDamage,
    uint AppliedDamage,
    int BeforeHealth,
    int AfterHealth,
    long BeforeVitalsRevision,
    long AfterVitalsRevision,
    long BeforeLifeRevision,
    long AfterLifeRevision,
    ulong LastAttackEventId);

/// <summary>
/// Owns one logical player's transport-neutral incoming-damage ECS. The
/// adapter copies scalar state in, applies an accepted decision to
/// GameCharacter under its vitals gate, and retains only ECS dedupe state.
/// </summary>
internal sealed class PlayerVitalsDamageEcsAdapter
{
    private readonly object _gate = new();
    private EcsWorld? _world;
    private EcsSystemScheduler? _scheduler;
    private MonsterPlayerDamageEntity _player;
    private int _characterId;
    private uint _objectId;
    private PlayerMonsterDamageEcsDecision? _lastDecision;

    public PlayerMonsterDamageEcsDecision? Snapshot()
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
            _player = default;
            _characterId = 0;
            _objectId = 0;
            _lastDecision = null;
        }
    }

    public PlayerMonsterDamageEcsDecision Apply(
        GameCharacter character,
        uint playerObjectId,
        long currentLifeRevision,
        in PlayerMonsterDamageEcsRequest request,
        Action? beforeLethalCommit = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentOutOfRangeException.ThrowIfNegative(
            currentLifeRevision);

        lock (_gate)
        {
            lock (character.VitalsSync)
            {
                var snapshot = SnapshotPlayer(
                    character,
                    playerObjectId,
                    currentLifeRevision);
                EnsureAttached(snapshot);
                var world = _world!;
                var scheduler = _scheduler!;
                MonsterPlayerDamageEcsBoundary.SynchronizePlayer(
                    world,
                    _player,
                    snapshot);
                MonsterPlayerDamageEcsBoundary.QueueDamage(
                    world,
                    _player,
                    new MonsterPlayerDamageIntentComponent(
                        request.AttackEventId,
                        request.MonsterObjectId,
                        request.MonsterSpawnGeneration,
                        request.ExpectedCharacterId,
                        request.ExpectedPlayerObjectId,
                        request.ExpectedLifeRevision,
                        request.ExpectedVitalsRevision,
                        request.ResolvedDamage));
                scheduler.RunTick(TimeSpan.Zero);

                var applied = scheduler.Events
                    .Read<MonsterPlayerDamageAppliedEvent>();
                var rejected = scheduler.Events
                    .Read<MonsterPlayerDamageRejectedEvent>();
                var deaths = scheduler.Events
                    .Read<MonsterPlayerDeathDecisionEvent>();
                if (applied.Length + rejected.Length != 1 ||
                    applied.Length > 1 ||
                    rejected.Length > 1)
                {
                    throw new InvalidOperationException(
                        "Incoming damage ECS did not emit exactly one decision.");
                }

                PlayerMonsterDamageEcsDecision decision;
                if (applied.Length == 1)
                {
                    var result = applied[0];
                    if (deaths.Length != (result.Killed ? 1 : 0))
                    {
                        throw new InvalidOperationException(
                            "Incoming damage ECS emitted an inconsistent death decision.");
                    }

                    if (result.Killed)
                    {
                        beforeLethalCommit?.Invoke();
                    }

                    character.CurrentHp = result.AfterHealth;
                    var appliedRevision =
                        character.MarkVitalsChanged();
                    if (appliedRevision !=
                        result.AfterVitalsRevision)
                    {
                        throw new InvalidOperationException(
                            "Incoming damage ECS and GameCharacter vitals revisions diverged.");
                    }

                    decision = new PlayerMonsterDamageEcsDecision(
                        Applied: true,
                        result.Killed,
                        MonsterPlayerDamageRejectionReason.None,
                        result.DecisionSequence,
                        result.AttackEventId,
                        result.MonsterObjectId,
                        result.RequestedDamage,
                        result.AppliedDamage,
                        result.BeforeHealth,
                        result.AfterHealth,
                        result.BeforeVitalsRevision,
                        result.AfterVitalsRevision,
                        result.BeforeLifeRevision,
                        result.AfterLifeRevision,
                        ReadLastAttackEventId(world));
                }
                else
                {
                    if (deaths.Length != 0)
                    {
                        throw new InvalidOperationException(
                            "Rejected incoming damage emitted a death decision.");
                    }

                    var result = rejected[0];
                    decision = new PlayerMonsterDamageEcsDecision(
                        Applied: false,
                        Killed: false,
                        result.Reason,
                        result.DecisionSequence,
                        result.AttackEventId,
                        result.MonsterObjectId,
                        request.ResolvedDamage,
                        AppliedDamage: 0,
                        result.CurrentHealth,
                        result.CurrentHealth,
                        result.CurrentVitalsRevision,
                        result.CurrentVitalsRevision,
                        result.CurrentLifeRevision,
                        result.CurrentLifeRevision,
                        result.LastAttackEventId);
                }

                _lastDecision = decision;
                return decision;
            }
        }
    }

    private void EnsureAttached(
        in MonsterPlayerDamageHydrationSnapshot snapshot)
    {
        if (_world is not null &&
            _world.IsAlive(_player.Entity) &&
            _characterId == snapshot.CharacterId &&
            _objectId == snapshot.PlayerObjectId)
        {
            return;
        }

        var world = new EcsWorld();
        var player =
            MonsterPlayerDamageEcsBoundary.HydratePlayer(
                world,
                snapshot);
        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new MonsterPlayerDamageSystem());
        _world = world;
        _scheduler = scheduler;
        _player = player;
        _characterId = snapshot.CharacterId;
        _objectId = snapshot.PlayerObjectId;
        _lastDecision = null;
    }

    private ulong ReadLastAttackEventId(EcsWorld world) =>
        world.Get<MonsterPlayerDamageStateComponent>(
            _player.Entity).LastAttackEventId;

    private static MonsterPlayerDamageHydrationSnapshot
        SnapshotPlayer(
            GameCharacter character,
            uint playerObjectId,
            long lifeRevision) =>
        new(
            character.Id,
            character.AccountId,
            playerObjectId,
            character.CurrentHp,
            character.MaxHp,
            character.CurrentMp,
            character.MaxMp,
            character.VitalsRevision,
            lifeRevision);
}
