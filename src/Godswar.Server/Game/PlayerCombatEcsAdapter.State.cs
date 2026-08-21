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
        GameSessionRegistry registry,
        GameCharacter character,
        uint objectId,
        DateTimeOffset nextBasicAttackAt,
        in ClientStatusAggregate runtimeModifiers)
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
                SnapshotOffense(character, runtimeModifiers),
                resources,
                new PlayerCommittedProgressionSnapshot(
                    character.Level,
                    character.Experience,
                    character.TalentExperience,
                    character.TalentPoints,
                    Revision: 0,
                    LastProjectionId: 0)));
        var scheduler = new EcsSystemScheduler(world);
        var accountId = character.AccountId;
        var characterId = character.Id;
        scheduler.AddSystem(new PlayerCombatIntentSystem(
            () => AdmitCombatAttempt(
                registry,
                accountId,
                characterId)));
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

    private ulong AdmitCombatAttempt(
        GameSessionRegistry registry,
        int accountId,
        int characterId)
    {
        var revision = registry.NextAdmittedCombatRevision(
            accountId,
            characterId);
        _onAdmittedAttempt?.Invoke();
        return revision;
    }

    private PlayerCombatResourceSnapshot SynchronizePlayer(
        GameCharacter character,
        DateTimeOffset nextBasicAttackAt,
        in ClientStatusAggregate runtimeModifiers)
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
        world.Set(_player, SnapshotOffense(character, runtimeModifiers));
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
        in PlayerCombatEcsRequest request)
    {
        var world = _world!;
        foreach (var entity in _targetEntities)
        {
            world.TryDestroyEntity(entity);
        }

        _targetEntities.Clear();
        MonsterRuntimeSnapshot? selected = null;
        var snapshots = request.Kind == PlayerCombatIntentKind.AreaSkill
            ? registry.GetMapMonsterSnapshots(
                session,
                character.CurrentMap)
            : registry.TryGetMonsterSnapshot(
                session,
                character.CurrentMap,
                request.TargetObjectId,
                out var target)
                ? [target]
                : [];
        foreach (var snapshot in snapshots)
        {
            var visible = registry.IsMonsterVisibleTo(
                session,
                snapshot.ObjectId,
                snapshot.SpawnGeneration);
            var combatProfile = registry.GameplayCatalogs
                .MonsterCombatProfiles
                .Resolve(snapshot.Definition)
                .ToTargetStats();
            combatProfile = registry.AdjustPveMonsterTargetStats(
                session,
                snapshot,
                request.RequestedAt,
                combatProfile);
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
                    request.Kind == PlayerCombatIntentKind.BasicAttack
                        ? PlayerCombatRules.ResolveBasicAttackRange(
                            (character.CalculatedStats ??
                             CharacterStats.FromCharacter(character))
                            .BasicAttackRange)
                        : PlayerCombatRules
                            .DefaultBasicAttackRange)
                {
                    Level = combatProfile.Level,
                    PhysicalDefense = combatProfile.PhysicalDefense,
                    MagicDefense = combatProfile.MagicDefense,
                    Dodge = combatProfile.Dodge,
                    CriticalResistance =
                        combatProfile.CriticalResistance,
                    PhysicalDamageReductionBasisPoints =
                        combatProfile
                            .PhysicalDamageReductionBasisPoints,
                    MagicDamageReductionBasisPoints =
                        combatProfile.MagicDamageReductionBasisPoints,
                    CriticalDamageReductionBasisPoints =
                        combatProfile
                            .CriticalDamageReductionBasisPoints,
                    PhysicalFlatAbsorption =
                        combatProfile.PhysicalFlatAbsorption,
                    MagicFlatAbsorption =
                        combatProfile.MagicFlatAbsorption,
                    CriticalDamageFlatReduction =
                        combatProfile.CriticalDamageFlatReduction,
                    DamageReboundBasisPoints =
                        combatProfile.DamageReboundBasisPoints,
                    DamageReboundFlat = combatProfile.DamageReboundFlat
                });
            _targetEntities.Add(entity);
            if (snapshot.ObjectId == request.TargetObjectId)
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
        GameCharacter character,
        in ClientStatusAggregate runtimeModifiers)
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
            stats.MagicAppendDamage)
        {
            Level = character.Level,
            Hit = SaturatingAddRating(stats.Hit, runtimeModifiers.Hit),
            Critical = SaturatingAddRating(
                stats.Critical,
                runtimeModifiers.CriticalAppend),
            IgnorePhysicalDefenseBasisPoints =
                stats.IgnorePhysicalDefense,
            IgnoreMagicDefenseBasisPoints = stats.IgnoreMagicDefense,
            CriticalDamageBasisPoints = stats.CriticalDamagePercent,
            CriticalDamageFlat = stats.CriticalDamageFlat,
            LifeAbsorptionBasisPoints = stats.LifeAbsorption,
            LifeAbsorptionFlat = stats.LifeAbsorptionFlat,
            BasicAttackIntervalMilliseconds =
                stats.BasicAttackIntervalMilliseconds
        };
    }

    private static int SaturatingAddRating(int baseValue, int modifier) =>
        (int)Math.Clamp(
            (long)baseValue + modifier,
            int.MinValue,
            int.MaxValue);

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
