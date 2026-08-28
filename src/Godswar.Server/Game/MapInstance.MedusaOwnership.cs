using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        private readonly ImmutableDictionary<
            MedusaOwnedMonsterIdentity,
            MedusaOwnedMonsterBinding> _bindings;
        private readonly ImmutableArray<MedusaOwnedMonsterBinding>
            _orderedBindings;
        private readonly MedusaEncounterMechanicsRuntime _mechanics;
        private readonly MedusaRunRuntime _run;
        private readonly int _playerCapacity;
        private readonly HashSet<int> _lateAdmittedCharacters = [];

        public MedusaInstanceOwnerBoundAggregate(
            WorldInstanceDescriptor descriptor,
            MedusaEncounterDifficulty difficulty,
            IReadOnlyCollection<int> admittedCharacterIds,
            IReadOnlyCollection<MedusaRunSpawnDefinition> spawns)
        {
            _run = new MedusaRunRuntime(
                descriptor.InstanceId,
                difficulty,
                admittedCharacterIds,
                spawns,
                descriptor.CreatedAt);
            _mechanics = new MedusaEncounterMechanicsRuntime(_run);
            _playerCapacity = descriptor.PlayerCapacity;

            var run = _run.Snapshot();
            _orderedBindings = run.Spawns
                .Select(spawn => new MedusaOwnedMonsterBinding(
                    new(
                        spawn.ObjectId,
                        spawn.SpawnGeneration),
                    spawn.RosterSpawnId,
                    spawn.TemplateKey,
                    spawn.Role,
                    spawn.Rank,
                    run.Difficulty,
                    run.ContentMapId))
                .OrderBy(static binding => binding.Identity.ObjectId)
                .ThenBy(static binding =>
                    binding.Identity.SpawnGeneration)
                .ToImmutableArray();
            _bindings = _orderedBindings.ToImmutableDictionary(
                static binding => binding.Identity);
        }

        public bool TryGetBinding(
            uint objectId,
            uint spawnGeneration,
            out MedusaOwnedMonsterBinding binding) =>
            _bindings.TryGetValue(
                new(objectId, spawnGeneration),
                out binding);

        public MedusaInstanceOwnershipSnapshot Snapshot()
        {
            EnsureCoupledClocks(out var run, out var mechanics);
            return new(
                run.WorldInstanceId,
                run.Difficulty,
                run.ContentMapId,
                _orderedBindings,
                run,
                mechanics);
        }
    }

    private readonly object _medusaOwnershipGate = new();
    private MedusaInstanceOwnerBoundAggregate? _medusaInstanceOwner;

    internal MedusaInstanceBindResult BindMedusaEncounter(
        MedusaEncounterDifficulty difficulty,
        IReadOnlyCollection<int> admittedCharacterIds,
        IReadOnlyCollection<MedusaRunSpawnDefinition> spawns)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is not null)
            {
                return RejectedBind(
                    MedusaInstanceBindOutcome.AlreadyBound);
            }

            lock (_descriptorGate)
            {
                var descriptor = _descriptor;
                var candidateOutcome = TryCreateMedusaOwnerCandidate(
                    descriptor,
                    difficulty,
                    admittedCharacterIds,
                    spawns,
                    out var candidate);
                if (candidateOutcome !=
                    MedusaInstanceBindOutcome.Bound)
                {
                    return RejectedBind(candidateOutcome);
                }

                lock (_membershipGate)
                {
                    if (HasMedusaPlayerMembershipOrStaging())
                    {
                        return RejectedBind(
                            MedusaInstanceBindOutcome.RuntimeNotEmpty);
                    }

                    lock (_monsterRuntimeGate)
                    {
                        if (_monsterRuntime is not null)
                        {
                            return RejectedBind(
                                MedusaInstanceBindOutcome
                                    .MonsterRuntimeAlreadyInitialized);
                        }

                        _medusaInstanceOwner = candidate;
                        return new(
                            MedusaInstanceBindOutcome.Bound,
                            candidate.Snapshot());
                    }
                }
            }
        }
    }

    // Must be called while _membershipGate is held. Legacy population is
    // session-backed, while a staged transfer is already reserved in the ECS
    // shadow. Both stores therefore participate in the empty-runtime fence.
    private bool HasMedusaPlayerMembershipOrStaging() =>
        !_sessions.IsEmpty || _ecsShadow.PlayerCount != 0;

    internal bool TryGetMedusaOwnershipSnapshot(
        out MedusaInstanceOwnershipSnapshot snapshot)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                snapshot = owner.Snapshot();
                return true;
            }

            snapshot = null!;
            return false;
        }
    }

    internal bool TryGetMedusaMonsterBinding(
        uint objectId,
        uint spawnGeneration,
        out MedusaOwnedMonsterBinding binding)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                return owner.TryGetBinding(
                    objectId,
                    spawnGeneration,
                    out binding);
            }

            binding = default;
            return false;
        }
    }

    private static MedusaInstanceBindResult RejectedBind(
        MedusaInstanceBindOutcome outcome) => new(
        outcome,
        Snapshot: null);

    private static MedusaInstanceBindOutcome
        TryCreateMedusaOwnerCandidate(
            WorldInstanceDescriptor descriptor,
            MedusaEncounterDifficulty difficulty,
            IReadOnlyCollection<int>? admittedCharacterIds,
            IReadOnlyCollection<MedusaRunSpawnDefinition>? spawns,
            out MedusaInstanceOwnerBoundAggregate candidate)
    {
        candidate = null!;
        if (descriptor.LifecycleState !=
            WorldInstanceLifecycleState.Creating)
        {
            return MedusaInstanceBindOutcome.LifecycleNotCreating;
        }
        if (descriptor.Kind != InstanceKind.Dungeon)
        {
            return MedusaInstanceBindOutcome.WrongInstanceKind;
        }
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var difficultyDefinition))
        {
            return MedusaInstanceBindOutcome.UnknownDifficulty;
        }
        if (descriptor.MapId != difficultyDefinition.ContentMapId)
        {
            return MedusaInstanceBindOutcome.ContentMapMismatch;
        }
        if (admittedCharacterIds is null || spawns is null)
        {
            return MedusaInstanceBindOutcome.InvalidRunDefinition;
        }
        if (admittedCharacterIds.Count > descriptor.PlayerCapacity)
        {
            return MedusaInstanceBindOutcome
                .AdmittedRosterExceedsPlayerCapacity;
        }

        try
        {
            candidate = new(
                descriptor,
                difficulty,
                admittedCharacterIds,
                spawns);
            return MedusaInstanceBindOutcome.Bound;
        }
        catch (ArgumentException)
        {
            return MedusaInstanceBindOutcome.InvalidRunDefinition;
        }
    }
}
