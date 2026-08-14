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
    uint ResolvedDamage,
    DateTimeOffset ResolvedAt = default,
    int HealingReceivedBasisPoints =
        ElementalBasisPointMath.Denominator);

internal readonly record struct PlayerPetHealingEcsDecision(
    long PetId,
    int PolicyVersion,
    int ResolvedHealing,
    int AppliedHealing,
    int BeforeHealth,
    int AfterHealth,
    long BeforeVitalsRevision,
    long AfterVitalsRevision,
    DateTimeOffset AppliedAt,
    DateTimeOffset CooldownReadyAt);

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
    ulong LastAttackEventId,
    PlayerPetHealingEcsDecision? PetHealing = null)
{
    public int FinalHealth =>
        PetHealing?.AfterHealth ?? AfterHealth;

    public long FinalVitalsRevision =>
        PetHealing?.AfterVitalsRevision ?? AfterVitalsRevision;
}

/// <summary>
/// Owns one logical player's transport-neutral incoming-damage ECS. The
/// adapter copies scalar state in, applies an accepted decision to
/// GameCharacter under its vitals gate, and retains only ECS dedupe state plus
/// the bounded active-pet projection. Cooldown ownership remains process-wide.
/// </summary>
internal sealed class PlayerVitalsDamageEcsAdapter
{
    private readonly object _gate = new();
    private readonly ProcessPetHealingCooldownStore _petHealingCooldowns;
    private EcsWorld? _world;
    private EcsSystemScheduler? _scheduler;
    private MonsterPlayerDamageEntity _player;
    private int _characterId;
    private uint _objectId;
    private PlayerMonsterDamageEcsDecision? _lastDecision;
    private PetHealingTalentHydrationSnapshot? _activePet;

    public PlayerVitalsDamageEcsAdapter(
        ProcessPetHealingCooldownStore petHealingCooldowns)
    {
        _petHealingCooldowns = petHealingCooldowns ??
            throw new ArgumentNullException(
                nameof(petHealingCooldowns));
    }

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

    public void UpdateActivePet(
        PetHealingTalentHydrationSnapshot? activePet)
    {
        lock (_gate)
        {
            _activePet = activePet;
            if (_world is not null &&
                _world.IsAlive(_player.Entity))
            {
                MonsterPlayerDamageEcsBoundary
                    .SynchronizePetHealingTalent(
                        _world,
                        _player,
                        _activePet);
            }
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
                MonsterPlayerDamageEcsBoundary
                    .SynchronizePetHealingTalent(
                        world,
                        _player,
                        _activePet);
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
                        request.ResolvedDamage,
                        request.ResolvedAt,
                        request.HealingReceivedBasisPoints));
                scheduler.RunTick(TimeSpan.Zero);

                var applied = scheduler.Events
                    .Read<MonsterPlayerDamageAppliedEvent>();
                var rejected = scheduler.Events
                    .Read<MonsterPlayerDamageRejectedEvent>();
                var deaths = scheduler.Events
                    .Read<MonsterPlayerDeathDecisionEvent>();
                var petHealing = scheduler.Events
                    .Read<PetHealingAppliedEvent>();
                if (applied.Length + rejected.Length != 1 ||
                    applied.Length > 1 ||
                    rejected.Length > 1 ||
                    petHealing.Length > 1)
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
                    if ((result.Killed && petHealing.Length != 0) ||
                        petHealing.Length == 1 &&
                        (petHealing[0].AttackEventId !=
                             result.AttackEventId ||
                         petHealing[0].BeforeHealth !=
                             result.AfterHealth ||
                         petHealing[0].BeforeVitalsRevision !=
                             result.AfterVitalsRevision))
                    {
                        throw new InvalidOperationException(
                            "Incoming damage ECS emitted an inconsistent pet-healing decision.");
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

                    PlayerPetHealingEcsDecision? healingDecision = null;
                    if (petHealing.Length == 1)
                    {
                        var healing = petHealing[0];
                        character.CurrentHp = healing.AfterHealth;
                        var healingRevision =
                            character.MarkVitalsChanged();
                        if (healingRevision !=
                            healing.AfterVitalsRevision)
                        {
                            throw new InvalidOperationException(
                                "Pet Healing ECS and GameCharacter vitals revisions diverged.");
                        }

                        healingDecision =
                            new PlayerPetHealingEcsDecision(
                                healing.PetId,
                                healing.PolicyVersion,
                                healing.ResolvedHealing,
                                healing.AppliedHealing,
                                healing.BeforeHealth,
                                healing.AfterHealth,
                                healing.BeforeVitalsRevision,
                                healing.AfterVitalsRevision,
                                healing.AppliedAt,
                                healing.CooldownReadyAt);
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
                        ReadLastAttackEventId(world),
                        healingDecision);
                }
                else
                {
                    if (deaths.Length != 0)
                    {
                        throw new InvalidOperationException(
                            "Rejected incoming damage emitted a death decision.");
                    }
                    if (petHealing.Length != 0)
                    {
                        throw new InvalidOperationException(
                            "Rejected incoming damage emitted pet Healing.");
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
        scheduler.AddSystem(
            new PetHealingTalentSystem(_petHealingCooldowns));
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
