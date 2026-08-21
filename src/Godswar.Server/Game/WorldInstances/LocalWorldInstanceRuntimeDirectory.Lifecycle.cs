using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class LocalWorldInstanceRuntimeDirectory
{
    public async ValueTask<WorldInstanceRuntimeDirectoryResult>
        BeginDrainAsync(
            WorldInstanceId instanceId,
            long expectedRevision,
            DateTimeOffset transitionedAt,
            CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!TryFind(instanceId, out var runtime))
            {
                return Result(
                    WorldInstanceRuntimeDirectoryStatus.InstanceNotFound);
            }

            var transition = await _placement.TransitionAsync(
                instanceId,
                expectedRevision,
                WorldInstanceLifecycleState.Draining,
                transitionedAt,
                cancellationToken);
            if (!transition.Succeeded ||
                transition.Placement is null)
            {
                return PlacementRejected(runtime, transition.Status);
            }

            runtime.BindDescriptor(
                transition.Placement.Descriptor);
            return Result(
                WorldInstanceRuntimeDirectoryStatus.Draining,
                runtime);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<WorldInstanceRuntimeDirectoryResult>
        CloseAsync(
            WorldInstanceId instanceId,
            long expectedRevision,
            DateTimeOffset transitionedAt,
            CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!TryFind(instanceId, out var runtime))
            {
                return Result(
                    WorldInstanceRuntimeDirectoryStatus.InstanceNotFound);
            }

            var descriptor = runtime.Descriptor;
            if (descriptor.Revision != expectedRevision)
            {
                return PlacementRejected(
                    runtime,
                    WorldInstancePlacementStatus.RevisionConflict);
            }
            if (transitionedAt.ToUniversalTime() <
                descriptor.LastTransitionAt)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transitionedAt),
                    transitionedAt,
                    "Lifecycle transition time cannot move backwards.");
            }
            if (runtime.Owner.GetSnapshot().State !=
                SingleOwnerMailboxState.Accepting)
            {
                return Result(
                    WorldInstanceRuntimeDirectoryStatus
                        .OwnerShutdownIncomplete,
                    runtime);
            }

            var population = runtime.Owner.Invoke(
                static map => map.Population,
                _ownerInvocationTimeout,
                cancellationToken);
            if (population != 0)
            {
                return Result(
                    WorldInstanceRuntimeDirectoryStatus.RuntimeNotEmpty,
                    runtime);
            }
            if (descriptor.LifecycleState !=
                WorldInstanceLifecycleState.Draining)
            {
                return PlacementRejected(
                    runtime,
                    WorldInstancePlacementStatus
                        .InvalidLifecycleTransition);
            }

            var placement = await _placement.FindAsync(
                instanceId,
                cancellationToken);
            if (placement is null)
            {
                return Result(
                    WorldInstanceRuntimeDirectoryStatus.InstanceNotFound);
            }
            if (placement.Population != 0)
            {
                return PlacementRejected(
                    runtime,
                    WorldInstancePlacementStatus.InstanceNotEmpty);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // This rejects new owner work. Finish or fail to a finite internal
            // deadline even if the caller cancels after this commit boundary.
            var shutdown = await runtime.Owner.ShutdownAsync(
                _ownerShutdownTimeout,
                CancellationToken.None);
            if (shutdown !=
                SingleOwnerMailboxShutdownStatus.Drained)
            {
                return Result(
                    WorldInstanceRuntimeDirectoryStatus
                        .OwnerShutdownIncomplete,
                    runtime);
            }

            // The owner is quiescent, so this defensive read cannot race a
            // command admitted between the first observation and owner drain.
            if (runtime.Map.Population != 0)
            {
                return Result(
                    WorldInstanceRuntimeDirectoryStatus.RuntimeNotEmpty,
                    runtime);
            }

            var transition = await _placement.TransitionAsync(
                instanceId,
                expectedRevision,
                WorldInstanceLifecycleState.Closed,
                transitionedAt,
                CancellationToken.None);
            if (!transition.Succeeded ||
                transition.Placement is null)
            {
                return PlacementRejected(runtime, transition.Status);
            }

            runtime.BindDescriptor(
                transition.Placement.Descriptor);
            return Result(
                WorldInstanceRuntimeDirectoryStatus.Closed,
                runtime);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<WorldInstanceRuntimeDirectoryResult>
        RemoveClosedAsync(
            WorldInstanceId instanceId,
            CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!TryFind(instanceId, out var runtime))
            {
                return Result(
                    WorldInstanceRuntimeDirectoryStatus.InstanceNotFound);
            }

            var removal = await _placement.RemoveClosedAsync(
                instanceId,
                cancellationToken);
            if (!removal.Succeeded)
            {
                return PlacementRejected(runtime, removal.Status);
            }

            lock (_indexGate)
            {
                _runtimes.Remove(instanceId);
                if (runtime.Kind == InstanceKind.OpenWorld &&
                    runtime.ContentMapId.TryGetLegacyValue(
                        out var legacyMapId) &&
                    _openWorldByRoute.TryGetValue(
                        (runtime.RealmId, legacyMapId),
                        out var projectedId) &&
                    projectedId == instanceId)
                {
                    _openWorldByRoute.Remove(
                        (runtime.RealmId, legacyMapId));
                }
            }

            await runtime.DisposeAsync();
            return Result(
                WorldInstanceRuntimeDirectoryStatus.Removed);
        }
        finally
        {
            _mutationGate.Release();
        }
    }
}
