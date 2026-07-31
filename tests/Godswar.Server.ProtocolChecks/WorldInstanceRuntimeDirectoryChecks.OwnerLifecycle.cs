using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldInstanceRuntimeDirectoryChecks
{
    private static readonly TimeSpan OwnerCheckTimeout =
        TimeSpan.FromSeconds(2);

    private static async Task CheckOwnerLifecycleAsync()
    {
        var placement = CreatePlacement(maximumInstances: 2);
        await using var directory =
            new LocalWorldInstanceRuntimeDirectory(
                placement,
                new MapWorldInstanceRuntimeFactory(
                    mailboxCapacity: 4,
                    mailboxShutdownTimeout:
                        TimeSpan.FromMilliseconds(100)),
                ownerInvocationTimeout:
                    TimeSpan.FromMilliseconds(25),
                ownerShutdownTimeout:
                    TimeSpan.FromMilliseconds(100));
        var runtime = await CreateInstancedAsync(
            directory,
            mapId: 60,
            InstanceKind.Dungeon);
        var draining = await DrainAsync(directory, runtime);

        var movedBackwards = false;
        try
        {
            await directory.CloseAsync(
                draining.InstanceId,
                draining.Descriptor.Revision,
                CreatedAt,
                default);
        }
        catch (ArgumentOutOfRangeException)
        {
            movedBackwards = true;
        }
        Check.True(
            movedBackwards &&
            draining.Owner.GetSnapshot().State ==
                SingleOwnerMailboxState.Accepting,
            "invalid transition time is rejected before owner shutdown");

        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = draining.Owner.TrySubmit(map =>
        {
            started.Set();
            release.Wait(OwnerCheckTimeout);
            return map.Population;
        });
        Check.True(
            started.Wait(OwnerCheckTimeout),
            "owner timeout fixture starts its accepted command");

        var timedOut = false;
        try
        {
            await directory.CloseAsync(
                draining.InstanceId,
                draining.Descriptor.Revision,
                CreatedAt.AddMinutes(2),
                default);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        Check.True(
            timedOut,
            "close population query observes its finite owner timeout");
        Check.True(
            draining.Descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Draining &&
            draining.Owner.GetSnapshot().State ==
                SingleOwnerMailboxState.Accepting,
            "owner timeout leaves a draining runtime available for retry");

        release.Set();
        await blocker.RequireCompletion().WaitAsync(OwnerCheckTimeout);
        var closed = await CloseAsync(directory, draining);
        var owner = closed.Owner.GetSnapshot();
        Check.True(
            owner.State == SingleOwnerMailboxState.Stopped &&
            owner.Depth == 0 &&
            owner.Active == 0,
            "successful close waits for owner quiescence");
        Check.True(
            closed.Descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Closed,
            "directory publishes Closed only after owner quiescence");
        Check.True(
            closed.Owner.TrySubmit(
                    static map => map.Population)
                .Status ==
                SingleOwnerMailboxAdmissionStatus.Stopped,
            "closed runtime cannot admit new map mutations");
        await RemoveAsync(directory, closed);

        var quarantined = await CreateInstancedAsync(
            directory,
            mapId: 61,
            InstanceKind.Dungeon);
        var quarantinedDraining =
            await DrainAsync(directory, quarantined);
        Check.True(
            quarantinedDraining.Owner.BeginDrain() ==
                SingleOwnerMailboxDrainStatus.Started,
            "fixture stops the owner outside directory close");
        var rejectedClose = await directory.CloseAsync(
            quarantinedDraining.InstanceId,
            quarantinedDraining.Descriptor.Revision,
            CreatedAt.AddMinutes(2),
            default);
        Check.True(
            rejectedClose.Status ==
                WorldInstanceRuntimeDirectoryStatus
                    .OwnerShutdownIncomplete &&
            quarantinedDraining.Descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Draining,
            "pre-stopped owner cannot be promoted to Closed");

        await CheckRegistryDisposalAsync();
    }

    private static async Task CheckRegistryDisposalAsync()
    {
        var registry = new GameSessionRegistry(
            worldInstanceOptions: new WorldInstanceRuntimeOptions
            {
                MaximumRuntimes = 2,
                MaximumPlayerAssignments = 4,
                MaximumRetiredInstanceIds = 8,
                DefaultOpenWorldPlayerCapacity = 4,
                MailboxCapacity = 4,
                OwnerInvocationTimeoutMilliseconds = 100,
                ShutdownDrainTimeoutMilliseconds = 100,
                MaximumFanoutConcurrency = 1
            });
        Check.Equal(
            0,
            registry.GetWorldInstanceDirectorySnapshot().RuntimeCount,
            "registry fixture creates an empty local directory");
        await registry.DisposeAsync();
        await registry.DisposeAsync();
        Check.Throws<ObjectDisposedException>(
            () => registry.GetWorldInstanceDirectorySnapshot(),
            "disposed registry cannot recreate a world directory");
    }
}
