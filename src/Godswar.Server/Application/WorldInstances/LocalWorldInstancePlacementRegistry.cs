using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Bounded, process-local implementation used while all simulations are owned
/// by one server process. Its contract can later sit behind a placement service;
/// it is not a distributed ownership authority.
/// </summary>
internal sealed class LocalWorldInstancePlacementRegistry :
    IWorldInstancePlacementRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<WorldInstanceId, Entry> _instances = [];
    private readonly Dictionary<int, WorldInstanceId> _characters = [];
    private readonly HashSet<WorldInstanceId> _retiredInstanceIds = [];
    private readonly Dictionary<(RealmId RealmId, MapId MapId), WorldInstanceId>
        _openWorldInstances = [];

    public LocalWorldInstancePlacementRegistry(
        ServerNodeId localNodeId,
        int maximumInstances,
        int maximumPlayerAssignments,
        int? maximumRetiredInstanceIds = null)
    {
        if (!localNodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid local server node ID is required.",
                nameof(localNodeId));
        }

        if (maximumInstances is <= 0 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInstances),
                maximumInstances,
                "Maximum instances must be between 1 and 65,536.");
        }

        if (maximumPlayerAssignments is <= 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPlayerAssignments),
                maximumPlayerAssignments,
                "Maximum player assignments must be between 1 and 1,000,000.");
        }

        var retiredLimit = maximumRetiredInstanceIds ??
            checked(maximumInstances * 8);
        if (retiredLimit is <= 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetiredInstanceIds),
                retiredLimit,
                "Maximum retired instance IDs must be between 1 and 1,000,000.");
        }

        LocalNodeId = localNodeId;
        MaximumInstances = maximumInstances;
        MaximumPlayerAssignments = maximumPlayerAssignments;
        MaximumRetiredInstanceIds = retiredLimit;
    }

    public ServerNodeId LocalNodeId { get; }

    public int MaximumInstances { get; }

    public int MaximumPlayerAssignments { get; }

    public int MaximumRetiredInstanceIds { get; }

    public ValueTask<WorldInstancePlacementResult> RegisterAsync(
        WorldInstanceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_retiredInstanceIds.Contains(descriptor.InstanceId))
            {
                return Result(
                    WorldInstancePlacementStatus.RetiredInstance);
            }

            if (_instances.TryGetValue(descriptor.InstanceId, out var existing))
            {
                return Result(
                    WorldInstancePlacementStatus.DuplicateInstance,
                    existing);
            }

            if (descriptor.LifecycleState !=
                WorldInstanceLifecycleState.Creating)
            {
                return Result(
                    WorldInstancePlacementStatus.InvalidLifecycleState);
            }

            if (_instances.Count >= MaximumInstances)
            {
                return Result(WorldInstancePlacementStatus.RegistryFull);
            }

            if (descriptor.PlayerCapacity > MaximumPlayerAssignments)
            {
                return Result(
                    WorldInstancePlacementStatus.PlayerRegistryFull);
            }

            var openWorldKey = (descriptor.RealmId, descriptor.MapId);
            if (descriptor.Kind == InstanceKind.OpenWorld &&
                _openWorldInstances.ContainsKey(openWorldKey))
            {
                return Result(
                    WorldInstancePlacementStatus.OpenWorldConflict);
            }

            var entry = new Entry(descriptor);
            _instances.Add(descriptor.InstanceId, entry);
            if (descriptor.Kind == InstanceKind.OpenWorld)
            {
                _openWorldInstances.Add(
                    openWorldKey,
                    descriptor.InstanceId);
            }

            return Result(
                WorldInstancePlacementStatus.Registered,
                entry);
        }
    }

    public ValueTask<WorldInstancePlacementResult> TransitionAsync(
        WorldInstanceId instanceId,
        long expectedRevision,
        WorldInstanceLifecycleState target,
        DateTimeOffset transitionedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(target))
        {
            return Result(
                WorldInstancePlacementStatus.InvalidLifecycleTransition);
        }

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
            {
                return Result(
                    WorldInstancePlacementStatus.InstanceNotFound);
            }

            if (entry.Descriptor.Revision != expectedRevision)
            {
                return Result(
                    WorldInstancePlacementStatus.RevisionConflict,
                    entry);
            }

            if (entry.Descriptor.LifecycleState == target)
            {
                return Result(
                    WorldInstancePlacementStatus.NoChange,
                    entry);
            }

            if (!entry.Descriptor.CanTransitionTo(target))
            {
                return Result(
                    WorldInstancePlacementStatus.InvalidLifecycleTransition,
                    entry);
            }

            if (target == WorldInstanceLifecycleState.Closed &&
                entry.Characters.Count != 0)
            {
                return Result(
                    WorldInstancePlacementStatus.InstanceNotEmpty,
                    entry);
            }

            entry.Descriptor = entry.Descriptor.TransitionTo(
                target,
                transitionedAt);

            return Result(
                WorldInstancePlacementStatus.Transitioned,
                entry);
        }
    }

    public ValueTask<WorldInstancePlacementResult> AssignCharacterAsync(
        int characterId,
        WorldInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        ValidateCharacterId(characterId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
            {
                return Result(
                    WorldInstancePlacementStatus.InstanceNotFound);
            }

            if (_characters.TryGetValue(characterId, out var current))
            {
                return Result(
                    current == instanceId
                        ? WorldInstancePlacementStatus.NoChange
                        : WorldInstancePlacementStatus.CharacterAlreadyAssigned,
                    _instances[current]);
            }

            var rejection = CanAccept(
                entry,
                addsPlayerAssignment: true);
            if (rejection is not null)
            {
                return Result(rejection.Value, entry);
            }

            entry.Characters.Add(characterId);
            _characters.Add(characterId, instanceId);
            return Result(
                WorldInstancePlacementStatus.Assigned,
                entry);
        }
    }

    public ValueTask<WorldInstancePlacementResult> TransferCharacterAsync(
        int characterId,
        WorldInstanceId expectedSourceInstanceId,
        WorldInstanceId targetInstanceId,
        CancellationToken cancellationToken)
    {
        ValidateCharacterId(characterId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out var current))
            {
                return Result(
                    WorldInstancePlacementStatus.AssignmentNotFound);
            }

            if (current == targetInstanceId)
            {
                return Result(
                    WorldInstancePlacementStatus.NoChange,
                    _instances[current]);
            }

            if (current != expectedSourceInstanceId)
            {
                return Result(
                    WorldInstancePlacementStatus.SourceMismatch,
                    _instances[current]);
            }

            if (!_instances.TryGetValue(
                    targetInstanceId,
                    out var target))
            {
                return Result(
                    WorldInstancePlacementStatus.InstanceNotFound);
            }

            var rejection = CanAccept(
                target,
                addsPlayerAssignment: false);
            if (rejection is not null)
            {
                return Result(rejection.Value, target);
            }

            var source = _instances[current];
            source.Characters.Remove(characterId);
            target.Characters.Add(characterId);
            _characters[characterId] = targetInstanceId;
            return Result(
                WorldInstancePlacementStatus.Transferred,
                target);
        }
    }

    public ValueTask<WorldInstancePlacementResult> ReleaseCharacterAsync(
        int characterId,
        WorldInstanceId expectedInstanceId,
        CancellationToken cancellationToken)
    {
        ValidateCharacterId(characterId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out var current))
            {
                return Result(
                    WorldInstancePlacementStatus.AssignmentNotFound);
            }

            if (current != expectedInstanceId)
            {
                return Result(
                    WorldInstancePlacementStatus.SourceMismatch,
                    _instances[current]);
            }

            var entry = _instances[current];
            entry.Characters.Remove(characterId);
            _characters.Remove(characterId);
            return Result(
                WorldInstancePlacementStatus.Released,
                entry);
        }
    }

    public ValueTask<WorldInstancePlacementResult> RemoveClosedAsync(
        WorldInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var entry))
            {
                return Result(
                    WorldInstancePlacementStatus.InstanceNotFound);
            }

            if (entry.Descriptor.LifecycleState !=
                WorldInstanceLifecycleState.Closed)
            {
                return Result(
                    WorldInstancePlacementStatus.InstanceNotClosed,
                    entry);
            }

            if (_retiredInstanceIds.Count >= MaximumRetiredInstanceIds)
            {
                return Result(
                    WorldInstancePlacementStatus.RetirementRegistryFull,
                    entry);
            }

            _instances.Remove(instanceId);
            _retiredInstanceIds.Add(instanceId);
            if (entry.Descriptor.Kind == InstanceKind.OpenWorld)
            {
                _openWorldInstances.Remove(
                    (entry.Descriptor.RealmId, entry.Descriptor.MapId));
            }

            return Result(WorldInstancePlacementStatus.Removed);
        }
    }

    public ValueTask<WorldInstancePlacementSnapshot?> FindAsync(
        WorldInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _instances.TryGetValue(instanceId, out var entry)
                    ? Snapshot(entry)
                    : null);
        }
    }

    public ValueTask<WorldInstancePlacementSnapshot?> FindCharacterAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        ValidateCharacterId(characterId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _characters.TryGetValue(characterId, out var instanceId)
                    ? Snapshot(_instances[instanceId])
                    : null);
        }
    }

    public IReadOnlyList<WorldInstancePlacementSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _instances.Values
                .Select(Snapshot)
                .OrderBy(static placement =>
                    placement.Descriptor.RealmId.Value)
                .ThenBy(static placement =>
                    placement.Descriptor.MapId.Value)
                .ThenBy(static placement =>
                    placement.Descriptor.InstanceId.Value)
                .ToArray();
        }
    }

    private WorldInstancePlacementStatus? CanAccept(
        Entry entry,
        bool addsPlayerAssignment)
    {
        if (entry.Descriptor.LifecycleState !=
            WorldInstanceLifecycleState.Active)
        {
            return WorldInstancePlacementStatus.InstanceNotActive;
        }

        if (addsPlayerAssignment &&
            _characters.Count >= MaximumPlayerAssignments)
        {
            return WorldInstancePlacementStatus.PlayerRegistryFull;
        }

        if (entry.Characters.Count >= entry.Descriptor.PlayerCapacity)
        {
            return WorldInstancePlacementStatus.InstanceFull;
        }

        return null;
    }

    private ValueTask<WorldInstancePlacementResult> Result(
        WorldInstancePlacementStatus status,
        Entry? entry = null) =>
        ValueTask.FromResult(
            new WorldInstancePlacementResult(
                status,
                entry is null ? null : Snapshot(entry)));

    private WorldInstancePlacementSnapshot Snapshot(Entry entry) =>
        new(
            entry.Descriptor,
            LocalNodeId,
            entry.Characters.Count);

    private static void ValidateCharacterId(int characterId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterId),
                characterId,
                "Character IDs must be positive.");
        }
    }

    private sealed class Entry(WorldInstanceDescriptor descriptor)
    {
        public WorldInstanceDescriptor Descriptor { get; set; } = descriptor;

        public HashSet<int> Characters { get; } = [];
    }
}
