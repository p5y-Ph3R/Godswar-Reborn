using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// A synchronous, short-lived view of an unpublished world instance. The
/// descriptor is an immutable creation snapshot. Bounded runtime operations
/// are valid only for the duration of the callback that received this view.
/// </summary>
internal interface IWorldInstanceRuntimePreparationContext
{
    WorldInstanceDescriptor Descriptor { get; }

    DateTimeOffset PreparedAt { get; }

    int Population { get; }

    MedusaMonsterAttachmentResult PrepareAndAttachMedusaProductionLive(
        MedusaEncounterDifficulty difficulty,
        IReadOnlyCollection<int> admittedCharacterIds,
        IReadOnlyCollection<MedusaRunSpawnDefinition> runSpawns,
        IReadOnlyList<CapturedMonsterSpawn> definitions);
}

/// <summary>
/// Builds and validates unpublished instance-local content while the runtime
/// descriptor is still Creating. A successful preparation is required before
/// the placement is registered or transitioned to Active.
/// </summary>
internal interface IWorldInstanceRuntimePreparation
{
    void Prepare(IWorldInstanceRuntimePreparationContext context);

    void ValidatePrepared(
        IWorldInstanceRuntimePreparationContext context);
}

/// <summary>
/// Revocable capability used for one synchronous preparation callback. It
/// deliberately exposes neither MapInstance, its mailbox, nor the enclosing
/// WorldInstanceRuntime. Future preparation commands must be bounded methods
/// on this type and must not return mutable runtime objects.
/// </summary>
internal sealed class WorldInstanceRuntimePreparationContext :
    IWorldInstanceRuntimePreparationContext
{
    private readonly object _leaseGate = new();
    private readonly int _owningManagedThreadId;
    private WorldInstanceRuntime? _runtime;

    public WorldInstanceRuntimePreparationContext(
        WorldInstanceRuntime runtime,
        WorldInstanceDescriptor descriptor)
    {
        _runtime = runtime ??
            throw new ArgumentNullException(nameof(runtime));
        Descriptor = descriptor ??
            throw new ArgumentNullException(nameof(descriptor));
        _owningManagedThreadId = Environment.CurrentManagedThreadId;
    }

    public WorldInstanceDescriptor Descriptor { get; }

    public DateTimeOffset PreparedAt => Descriptor.CreatedAt;

    public int Population => InvokeRuntime(
        static runtime => runtime.Map.Population);

    public MedusaMonsterAttachmentResult
        PrepareAndAttachMedusaProductionLive(
            MedusaEncounterDifficulty difficulty,
            IReadOnlyCollection<int> admittedCharacterIds,
            IReadOnlyCollection<MedusaRunSpawnDefinition> runSpawns,
            IReadOnlyList<CapturedMonsterSpawn> definitions) =>
        InvokeRuntime(runtime =>
            runtime.Map.PrepareAndAttachMedusaProductionLive(
                difficulty,
                admittedCharacterIds,
                runSpawns,
                definitions));

    internal MedusaMonsterAttachmentResult
        PrepareAndAttachMedusaForAuthoredValidationTests(
            MedusaEncounterDifficulty difficulty,
            IReadOnlyCollection<int> admittedCharacterIds,
            IReadOnlyCollection<MedusaRunSpawnDefinition> runSpawns,
            IReadOnlyList<CapturedMonsterSpawn> definitions) =>
        InvokeRuntime(runtime =>
            runtime.Map.PrepareAndAttachMedusaForAuthoredValidationTests(
                difficulty,
                admittedCharacterIds,
                runSpawns,
                definitions));

    internal void Invalidate()
    {
        lock (_leaseGate)
        {
            _runtime = null;
        }
    }

    private TResult InvokeRuntime<TResult>(
        Func<WorldInstanceRuntime, TResult> operation)
    {
        lock (_leaseGate)
        {
            ObjectDisposedException.ThrowIf(
                _runtime is null,
                this);
            if (Environment.CurrentManagedThreadId !=
                _owningManagedThreadId)
            {
                throw new InvalidOperationException(
                    "World-instance preparation capabilities are " +
                    "synchronous and thread-affine.");
            }

            return operation(_runtime);
        }
    }
}

/// <summary>
/// Registry-facing immutable result for prepared creation. Runtime-directory
/// internals may retain a runtime handle, but callers of GameSessionRegistry
/// receive only the published descriptor snapshot and its exact identity.
/// </summary>
internal readonly record struct PreparedWorldInstanceCreationResult(
    WorldInstanceRuntimeDirectoryStatus Status,
    WorldInstanceDescriptor? Descriptor,
    WorldInstancePlacementStatus? PlacementStatus = null)
{
    public bool Succeeded => Status is
        WorldInstanceRuntimeDirectoryStatus.Created or
        WorldInstanceRuntimeDirectoryStatus.ExistingDefault or
        WorldInstanceRuntimeDirectoryStatus.Draining or
        WorldInstanceRuntimeDirectoryStatus.Closed or
        WorldInstanceRuntimeDirectoryStatus.Removed;

    public WorldInstanceId? InstanceId => Descriptor?.InstanceId;
}
