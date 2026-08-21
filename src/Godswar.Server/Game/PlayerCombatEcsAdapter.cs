
using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Boundaries.Combat;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

/// <summary>
/// Copies live session state into the transport-neutral combat ECS, lets ECS
/// reserve resources and produce guarded damage intents, applies those intents
/// through the shared monster runtime, and reconciles the mutation outcomes.
/// </summary>
internal sealed partial class PlayerCombatEcsAdapter
{
    private readonly object _gate = new();
    private readonly List<EntityId> _targetEntities = [];
    private readonly Dictionary<KillKey, PlayerCombatKillGuard> _killGuards = [];
    private EcsWorld? _world;
    private EcsSystemScheduler? _scheduler;
    private EntityId _player;
    private int _characterId;
    private uint _objectId;
    private ulong _nextIntentId;
    private ulong _nextProjectionId;
    private Action? _onAdmittedAttempt;
    private PlayerCombatEcsDecision? _lastDecision;
    private PlayerCombatEcsProjectionDecision? _lastProjection;

    public PlayerCombatEcsDecision? Snapshot()
    {
        lock (_gate)
        {
            return _lastDecision;
        }
    }

    public PlayerCombatEcsProjectionDecision? ProjectionSnapshot()
    {
        lock (_gate)
        {
            return _lastProjection;
        }
    }

    public PlayerCombatEcsDecision Execute(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character,
        uint objectId,
        DateTimeOffset nextBasicAttackAt,
        in PlayerCombatEcsRequest request,
        in ClientStatusAggregate runtimeModifiers,
        Action? onAdmittedAttempt = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);

        lock (_gate)
        {
            EnsureAttached(
                registry,
                character,
                objectId,
                nextBasicAttackAt,
                runtimeModifiers);
            var world = _world!;
            var scheduler = _scheduler!;
            var mirroredResources = SynchronizePlayer(
                character,
                nextBasicAttackAt,
                runtimeModifiers);
            var selectedTarget = HydrateTargets(
                registry,
                session,
                character,
                request);
            var intentId = NextIntentId();
            PlayerCombatEcsBoundary.QueueIntent(
                world,
                _player,
                new PlayerCombatIntentComponent(
                    intentId,
                    request.Kind,
                    request.RequestedAt,
                    request.TargetObjectId,
                    request.ExpectedTargetSpawnGeneration ??
                        selectedTarget?.SpawnGeneration ??
                        0,
                    selectedTarget?.HealthRevision ?? 0,
                    request.ReportedAttackerX,
                    request.ReportedAttackerZ,
                    request.HasReportedTargetPosition,
                    request.ReportedTargetX,
                    request.ReportedTargetZ,
                    request.Skill));
            _onAdmittedAttempt = onAdmittedAttempt;
            try
            {
                scheduler.RunTick(TimeSpan.Zero);
            }
            finally
            {
                _onAdmittedAttempt = null;
            }

            var rejection = SingleOrDefault<
                PlayerCombatIntentRejectedEvent>(
                scheduler.Events,
                "combat intent rejection");
            var reserved = SingleOrDefault<
                PlayerCombatResourceReservedEvent>(
                scheduler.Events,
                "combat resource reservation");
            var completed = SingleOrDefault<
                PlayerCombatReservationCompletedEvent>(
                scheduler.Events,
                "combat reservation completion");
            var damageIntents = scheduler.Events
                .Read<PlayerCombatDamageIntentEvent>()
                .ToArray();
            var resolvedTargets = scheduler.Events
                .Read<PlayerCombatTargetResolvedEvent>()
                .ToArray()
                .OrderBy(static value => value.TargetOrder)
                .ToArray();
            AdjustElementalPveDamageReservations(
                registry,
                session,
                character,
                request,
                intentId,
                resolvedTargets,
                damageIntents);
            var resourcesRefunded =
                scheduler.Events.Count<
                    PlayerCombatResourceRefundedEvent>() > 0;
            MirrorResourceDelta(
                character,
                ref mirroredResources,
                ReadResources());

            var hits = ImmutableArray.CreateBuilder<
                PlayerCombatEcsHit>(damageIntents.Length);
            var mutationRejection =
                PlayerCombatMutationRejectionReason.None;
            foreach (var damageIntent in damageIntents)
            {
                var outcome = ApplyMutation(
                    registry,
                    damageIntent,
                    out var damageResult);
                PlayerCombatEcsBoundary.QueueMutationOutcome(
                    world,
                    _player,
                    outcome);
                scheduler.RunTick(TimeSpan.Zero);

                var committed = SingleOrDefault<
                    PlayerCombatTargetMutationCommittedEvent>(
                    scheduler.Events,
                    "committed target mutation");
                var rejected = SingleOrDefault<
                    PlayerCombatTargetMutationRejectedEvent>(
                    scheduler.Events,
                    "rejected target mutation");
                var outcomeIgnored = SingleOrDefault<
                    PlayerCombatMutationOutcomeIgnoredEvent>(
                    scheduler.Events,
                    "ignored mutation outcome");
                if (outcomeIgnored is not null)
                {
                    throw new InvalidOperationException(
                        $"ECS ignored live mutation intent {intentId} " +
                        $"for target {damageIntent.TargetObjectId}: " +
                        $"{outcomeIgnored.Value.Reason}.");
                }

                if (outcome.Applied)
                {
                    if (damageResult is null || committed is null)
                    {
                        throw new InvalidOperationException(
                            "A committed monster mutation was not accepted " +
                            "by the combat ECS.");
                    }

                    PlayerCombatKillGuard? killGuard = null;
                    var killed = SingleOrDefault<
                        MonsterKilledByPlayerCombatEvent>(
                        scheduler.Events,
                        "monster kill");
                    if (killed is not null)
                    {
                        killGuard = new PlayerCombatKillGuard(
                            killed.Value.CombatIntentId,
                            killed.Value.MonsterObjectId,
                            killed.Value.MonsterSpawnGeneration,
                            killed.Value.MonsterHealthRevision);
                        _killGuards[new KillKey(
                            killGuard.Value.MonsterObjectId,
                            killGuard.Value.MonsterSpawnGeneration,
                            killGuard.Value.MonsterHealthRevision)] =
                            killGuard.Value;
                    }

                    hits.Add(new PlayerCombatEcsHit(
                        damageResult,
                        committed.Value.ReportedDamage,
                        killGuard));
                }
                else if (committed is not null)
                {
                    throw new InvalidOperationException(
                        "ECS committed a rejected monster mutation.");
                }

                if (rejected is not null)
                {
                    mutationRejection = rejected.Value.Reason;
                }

                var mutationCompleted = SingleOrDefault<
                    PlayerCombatReservationCompletedEvent>(
                    scheduler.Events,
                    "combat reservation completion");
                completed = mutationCompleted ?? completed;
                resourcesRefunded |= scheduler.Events.Count<
                    PlayerCombatResourceRefundedEvent>() > 0;
                MirrorResourceDelta(
                    character,
                    ref mirroredResources,
                    ReadResources());
            }

            if (world.Has<PlayerCombatReservationComponent>(_player))
            {
                throw new InvalidOperationException(
                    $"Combat reservation {intentId} remained open after " +
                    "all guarded mutation outcomes were supplied.");
            }

            var finalResources = ReadResources();
            var (currentMana, vitalsRevision) =
                ReadCharacterResources(character);
            var decision = new PlayerCombatEcsDecision(
                intentId,
                request.Kind,
                rejection?.Reason ??
                    PlayerCombatRejectionReason.None,
                mutationRejection,
                reserved?.TargetCount ?? 0,
                completed?.AcceptedTargetCount ?? hits.Count,
                completed?.RejectedTargetCount ?? 0,
                reserved?.ReservedMana ?? 0,
                resourcesRefunded,
                currentMana,
                vitalsRevision,
                finalResources.NextBasicAttackAt,
                hits.ToImmutable(),
                resolvedTargets.Select(static resolved =>
                        new PlayerCombatEcsResolvedTarget(
                            resolved.TargetObjectId,
                            resolved.ExpectedSpawnGeneration,
                            resolved.ExpectedHealthRevision,
                            resolved.Resolution))
                    .ToImmutableArray());
            _lastDecision = decision;
            return decision;
        }
    }

    public PlayerCombatEcsProjectionDecision ProjectCommittedProgression(
        MonsterDamageResult damageResult,
        CharacterProgressionResult committed)
    {
        ArgumentNullException.ThrowIfNull(damageResult);
        ArgumentNullException.ThrowIfNull(committed);

        lock (_gate)
        {
            if (_world is null ||
                !_world.IsAlive(_player) ||
                damageResult.HealthMutation is not { } mutation ||
                !_killGuards.TryGetValue(
                    new KillKey(
                        damageResult.ObjectId,
                        mutation.SpawnGeneration,
                        mutation.AfterHealthRevision),
                    out var guard))
            {
                return RecordProjection(
                    Applied: false,
                    ProjectionId: 0,
                    MonsterKillProgressionRejectionReason
                        .KillGuardMissing);
            }

            var world = _world;
            var scheduler = _scheduler!;
            var progression = world
                .Get<PlayerCommittedProgressionComponent>(_player);
            var projectionId = NextProjectionId();
            PlayerCombatEcsBoundary.QueueCommittedProgression(
                world,
                _player,
                projectionId,
                guard,
                progression.Revision,
                committed);
            scheduler.RunTick(TimeSpan.Zero);

            var applied = SingleOrDefault<
                MonsterKillProgressionAppliedEvent>(
                scheduler.Events,
                "committed progression projection");
            var rejected = SingleOrDefault<
                MonsterKillProgressionRejectedEvent>(
                scheduler.Events,
                "rejected progression projection");
            if (applied is not null)
            {
                _killGuards.Remove(new KillKey(
                    guard.MonsterObjectId,
                    guard.MonsterSpawnGeneration,
                    guard.MonsterHealthRevision));
                return RecordProjection(
                    Applied: true,
                    projectionId,
                    MonsterKillProgressionRejectionReason.None);
            }

            return RecordProjection(
                Applied: false,
                projectionId,
                rejected?.Reason ??
                    MonsterKillProgressionRejectionReason
                        .KillGuardMissing);
        }
    }

}
