using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

internal enum WorldInstanceRuntimeDirectoryStatus : byte
{
    Created = 1,
    ExistingDefault = 2,
    Draining = 3,
    Closed = 4,
    Removed = 5,
    InstanceNotFound = 20,
    InvalidInstanceKind = 21,
    LegacyMapUnsupported = 22,
    DefaultUnavailable = 23,
    RuntimeNotEmpty = 24,
    PlacementRejected = 25,
    OwnerShutdownIncomplete = 26
}

internal readonly record struct WorldInstanceRuntimeDirectoryResult(
    WorldInstanceRuntimeDirectoryStatus Status,
    WorldInstanceRuntime? Runtime,
    WorldInstancePlacementStatus? PlacementStatus = null)
{
    public bool Succeeded => Status is
        WorldInstanceRuntimeDirectoryStatus.Created or
        WorldInstanceRuntimeDirectoryStatus.ExistingDefault or
        WorldInstanceRuntimeDirectoryStatus.Draining or
        WorldInstanceRuntimeDirectoryStatus.Closed or
        WorldInstanceRuntimeDirectoryStatus.Removed;
}

internal readonly record struct WorldInstanceRuntimeDirectorySnapshot(
    int RuntimeCount,
    int OpenWorldCount,
    int MaximumRuntimes);
