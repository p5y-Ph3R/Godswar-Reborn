using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class PlayerCombatEcsAdapter
{

    private void EnsureAttached(
        GameCharacter character,
        uint objectId,
        DateTimeOffset nextBasicAttackAt)
    {
        if (_world is not null &&
            _world.IsAlive(_player) &&
            _characterId == character.Id &&
            _objectId == objectId)
        {
            return;
        }

        var world = new EcsWorld();
        var resources = SnapshotResources(
            character,
            nextBasicAttackAt,
            combatRevision: 0,
            eventSequence: 0);
        var player = PlayerCombatEcsBoundary.HydratePlayer(
            world,
            new PlayerCombatHydrationSnapshot(
                new PlayerCombatIdentityComponent(
                    character.Id,
                    objectId),
                new PlayerCombatTransformComponent(
                    character.CurrentMap,
                    character.PositionX,
                    character.PositionZ),
                SnapshotOffense(character),
                resources,
                new PlayerCommittedProgressionSnapshot(
                    character.Level,
                    character.Experience,
                    character.TalentExperience,
                    character.TalentPoints,
                    Revision: 0,
                    LastProjectionId: 0)));
        var scheduler = new EcsSystemScheduler(world);
        scheduler.AddSystem(new PlayerCombatIntentSystem());
        scheduler.AddSystem(new PlayerCombatMutationOutcomeSystem());
        scheduler.AddSystem(
            new MonsterKillProgressionProjectionSystem());
        _world = world;
        _scheduler = scheduler;
        _player = player;
        _characterId = character.Id;
        _objectId = objectId;
        _targetEntities.Clear();
        _killGuards.Clear();
        _lastDecision = null;
        _lastProjection = null;
    }

    private PlayerCombatResourceSnapshot SynchronizePlayer(
        GameCharacter character,
        DateTimeOffset nextBasicAttackAt)
    {
        var world = _world!;
        var previous = world
            .Get<PlayerCombatResourceComponent>(_player);
        var resources = SnapshotResources(
            character,
            nextBasicAttackAt,
            previous.CombatRevision,
            previous.EventSequence);
        world.Set(
            _player,
            new PlayerCombatTransformComponent(
                character.CurrentMap,
                character.PositionX,
                character.PositionZ));
        world.Set(_player, SnapshotOffense(character));
        world.Set(
            _player,
            new PlayerCombatResourceComponent(
                resources.CurrentHp,
                resources.MaximumHp,
                resources.CurrentMp,
                resources.MaximumMp,
                resources.VitalsRevision,
                resources.NextBasicAttackAt,
                resources.CombatRevision,
                resources.EventSequence));
        ref var progression = ref world
            .Get<PlayerCommittedProgressionComponent>(_player);
        progression.Level = character.Level;
        progression.Experience = character.Experience;
        progression.TalentExperience = character.TalentExperience;
        progression.TalentPoints = character.TalentPoints;
        return resources;
    }

    private MonsterRuntimeSnapshot? HydrateTargets(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character,
        PlayerCombatIntentKind kind,
        uint targetObjectId)
    {
        var world = _world!;
        foreach (var entity in _targetEntities)
        {
            world.TryDestroyEntity(entity);
        }

        _targetEntities.Clear();
        MonsterRuntimeSnapshot? selected = null;
        var snapshots = kind == PlayerCombatIntentKind.AreaSkill
            ? registry.GetMapMonsterSnapshots(
                session,
                character.CurrentMap)
            : registry.TryGetMonsterSnapshot(
                session,
                character.CurrentMap,
                targetObjectId,
                out var target)
                ? [target]
                : [];
        foreach (var snapshot in snapshots)
        {
            var visible = registry.IsMonsterVisibleTo(
                session,
                snapshot.ObjectId,
                snapshot.SpawnGeneration);
            var entity = PlayerCombatEcsBoundary.HydrateTarget(
                world,
                new PlayerCombatTargetComponent(
                    snapshot.ObjectId,
                    character.CurrentMap,
                    snapshot.X,
                    snapshot.Z,
                    snapshot.CurrentHealth,
                    snapshot.IsSpawned,
                    snapshot.IsAlive,
                    visible,
                    snapshot.SpawnGeneration,
                    snapshot.HealthRevision,
                    kind == PlayerCombatIntentKind.BasicAttack
                        ? MonsterCombatResolver
                            .ResolvePlayerBasicAttackRange(
                                snapshot.Definition,
                                registry.GameplayCatalogs
                                    .MonsterCombatRanges)
                        : PlayerCombatRules
                            .DefaultBasicAttackRange));
            _targetEntities.Add(entity);
            if (snapshot.ObjectId == targetObjectId)
            {
                selected = snapshot;
            }
        }

        return selected;
    }

    private PlayerCombatMutationOutcomeComponent ApplyMutation(
        GameSessionRegistry registry,
        in PlayerCombatDamageIntentEvent intent,
        out MonsterDamageResult? result)
    {
        if (registry.TryApplyMonsterDamageGuarded(
                intent.MapId,
                intent.TargetObjectId,
                intent.RequestedDamage,
                intent.CharacterId,
                intent.ExpectedSpawnGeneration,
                intent.ExpectedHealthRevision,
                DateTimeOffset.UtcNow,
                out var applied) &&
            applied.HealthMutation is { } mutation &&
            applied.BeforeHealth != applied.AfterHealth)
        {
            result = applied;
            return new PlayerCombatMutationOutcomeComponent(
                intent.IntentId,
                intent.TargetOrder,
                intent.TargetObjectId,
                mutation.SpawnGeneration,
                mutation.BeforeHealthRevision,
                Applied: true,
                applied.BeforeHealth,
                applied.AfterHealth,
                mutation.AfterHealthRevision,
                applied.Killed,
                PlayerCombatMutationRejectionReason.None);
        }

        result = null;
        var rejection =
            PlayerCombatMutationRejectionReason.TargetRejected;
        if (registry.TryGetMonsterSnapshot(
                intent.MapId,
                intent.TargetObjectId,
                intent.CharacterId,
                out var current))
        {
            if (current.SpawnGeneration !=
                intent.ExpectedSpawnGeneration)
            {
                rejection =
                    PlayerCombatMutationRejectionReason
                        .GenerationMismatch;
            }
            else if (current.HealthRevision !=
                     intent.ExpectedHealthRevision)
            {
                rejection =
                    PlayerCombatMutationRejectionReason
                        .RevisionMismatch;
            }
        }

        return new PlayerCombatMutationOutcomeComponent(
            intent.IntentId,
            intent.TargetOrder,
            intent.TargetObjectId,
            intent.ExpectedSpawnGeneration,
            intent.ExpectedHealthRevision,
            Applied: false,
            BeforeHealth: 0,
            AfterHealth: 0,
            AfterHealthRevision: intent.ExpectedHealthRevision,
            Killed: false,
            rejection);
    }

    private PlayerCombatResourceSnapshot ReadResources()
    {
        var resources = _world!
            .Get<PlayerCombatResourceComponent>(_player);
        return new PlayerCombatResourceSnapshot(
            resources.CurrentHp,
            resources.MaximumHp,
            resources.CurrentMp,
            resources.MaximumMp,
            resources.VitalsRevision,
            resources.NextBasicAttackAt,
            resources.CombatRevision,
            resources.EventSequence);
    }

    private static PlayerCombatResourceSnapshot SnapshotResources(
        GameCharacter character,
        DateTimeOffset nextBasicAttackAt,
        ulong combatRevision,
        ulong eventSequence)
    {
        lock (character.VitalsSync)
        {
            return new PlayerCombatResourceSnapshot(
                character.CurrentHp,
                character.MaxHp,
                character.CurrentMp,
                character.MaxMp,
                character.VitalsRevision,
                nextBasicAttackAt,
                combatRevision,
                eventSequence);
        }
    }

    private static PlayerCombatOffenseComponent SnapshotOffense(
        GameCharacter character)
    {
        var stats = character.CalculatedStats ??
                    CharacterStats.FromCharacter(character);
        return new PlayerCombatOffenseComponent(
            character.Profession,
            stats.PhysicalAttack,
            stats.MagicAttack,
            stats.PhysicalDamageBonus,
            stats.MagicDamageBonus,
            stats.PhysicalAppendDamage,
            stats.MagicAppendDamage);
    }

    private static void MirrorResourceDelta(
        GameCharacter character,
        ref PlayerCombatResourceSnapshot mirrored,
        in PlayerCombatResourceSnapshot current)
    {
        var manaDelta = current.CurrentMp - mirrored.CurrentMp;
        var revisionDelta =
            current.VitalsRevision - mirrored.VitalsRevision;
        if (revisionDelta < 0)
        {
            throw new InvalidOperationException(
                "Combat ECS vitals revision moved backwards.");
        }

        if (manaDelta != 0 || revisionDelta != 0)
        {
            lock (character.VitalsSync)
            {
                character.CurrentMp = (int)Math.Clamp(
                    (long)character.CurrentMp + manaDelta,
                    0L,
                    Math.Max(0, character.MaxMp));
                character.VitalsRevision = checked(
                    character.VitalsRevision + revisionDelta);
            }
        }

        mirrored = current;
    }

    private static (int CurrentMana, long VitalsRevision)
        ReadCharacterResources(GameCharacter character)
    {
        lock (character.VitalsSync)
        {
            return (
                character.CurrentMp,
                character.VitalsRevision);
        }
    }

    private static T? SingleOrDefault<T>(
        EcsEventBuffer events,
        string description)
        where T : struct
    {
        var values = events.Read<T>();
        if (values.Length > 1)
        {
            throw new InvalidOperationException(
                $"One ECS tick emitted multiple {description} events.");
        }

        return values.Length == 1 ? values[0] : null;
    }

    private ulong NextIntentId()
    {
        _nextIntentId = checked(_nextIntentId + 1);
        return _nextIntentId;
    }

    private ulong NextProjectionId()
    {
        _nextProjectionId = checked(_nextProjectionId + 1);
        return _nextProjectionId;
    }

    private PlayerCombatEcsProjectionDecision RecordProjection(
        bool Applied,
        ulong ProjectionId,
        MonsterKillProgressionRejectionReason RejectionReason)
    {
        var decision = new PlayerCombatEcsProjectionDecision(
            Applied,
            ProjectionId,
            RejectionReason);
        _lastProjection = decision;
        return decision;
    }

    private readonly record struct KillKey(
        uint ObjectId,
        uint SpawnGeneration,
        ulong HealthRevision);
}
