using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal enum WorldInstancePlacementStatus : byte
{
    Registered = 1,
    Transitioned = 2,
    Assigned = 3,
    Transferred = 4,
    Released = 5,
    Removed = 6,
    NoChange = 7,
    RegistryFull = 20,
    DuplicateInstance = 21,
    OpenWorldConflict = 22,
    InstanceNotFound = 23,
    InvalidLifecycleState = 24,
    InvalidLifecycleTransition = 25,
    RevisionConflict = 26,
    InstanceNotActive = 27,
    InstanceFull = 28,
    PlayerRegistryFull = 29,
    CharacterAlreadyAssigned = 30,
    AssignmentNotFound = 31,
    SourceMismatch = 32,
    InstanceNotClosed = 33,
    InstanceNotEmpty = 34,
    RetiredInstance = 35,
    RetirementRegistryFull = 36
}

internal sealed record WorldInstancePlacementSnapshot(
    WorldInstanceDescriptor Descriptor,
    ServerNodeId NodeId,
    int Population);

internal readonly record struct WorldInstancePlacementResult(
    WorldInstancePlacementStatus Status,
    WorldInstancePlacementSnapshot? Placement)
{
    public bool Succeeded => Status is
        WorldInstancePlacementStatus.Registered or
        WorldInstancePlacementStatus.Transitioned or
        WorldInstancePlacementStatus.Assigned or
        WorldInstancePlacementStatus.Transferred or
        WorldInstancePlacementStatus.Released or
        WorldInstancePlacementStatus.Removed or
        WorldInstancePlacementStatus.NoChange;
}

/// <summary>
/// Application boundary for locating runtime simulations. Durable realm and
/// character data remain outside this disposable coordination contract.
/// </summary>
internal interface IWorldInstancePlacementRegistry
{
    ServerNodeId LocalNodeId { get; }

    int MaximumInstances { get; }

    int MaximumPlayerAssignments { get; }

    int MaximumRetiredInstanceIds { get; }

    ValueTask<WorldInstancePlacementResult> RegisterAsync(
        WorldInstanceDescriptor descriptor,
        CancellationToken cancellationToken);

    ValueTask<WorldInstancePlacementResult> TransitionAsync(
        WorldInstanceId instanceId,
        long expectedRevision,
        WorldInstanceLifecycleState target,
        DateTimeOffset transitionedAt,
        CancellationToken cancellationToken);

    ValueTask<WorldInstancePlacementResult> AssignCharacterAsync(
        int characterId,
        WorldInstanceId instanceId,
        CancellationToken cancellationToken);

    ValueTask<WorldInstancePlacementResult> TransferCharacterAsync(
        int characterId,
        WorldInstanceId expectedSourceInstanceId,
        WorldInstanceId targetInstanceId,
        CancellationToken cancellationToken);

    ValueTask<WorldInstancePlacementResult> ReleaseCharacterAsync(
        int characterId,
        WorldInstanceId expectedInstanceId,
        CancellationToken cancellationToken);

    ValueTask<WorldInstancePlacementResult> RemoveClosedAsync(
        WorldInstanceId instanceId,
        CancellationToken cancellationToken);

    ValueTask<WorldInstancePlacementSnapshot?> FindAsync(
        WorldInstanceId instanceId,
        CancellationToken cancellationToken);

    ValueTask<WorldInstancePlacementSnapshot?> FindCharacterAsync(
        int characterId,
        CancellationToken cancellationToken);

    IReadOnlyList<WorldInstancePlacementSnapshot> Snapshot();
}
