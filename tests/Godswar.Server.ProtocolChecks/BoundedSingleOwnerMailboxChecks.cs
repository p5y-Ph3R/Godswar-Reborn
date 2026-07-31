using Godswar.Server.Application.WorldInstances;
using System.Collections.Concurrent;

namespace Godswar.Server.ProtocolChecks;

internal static class BoundedSingleOwnerMailboxChecks
{
    public const string CheckName =
        "B18B bounded on-demand single-owner mailbox";

    private static readonly TimeSpan Timeout =
        TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        await CheckConcurrentSingleOwnerAsync();
        await CheckFifoAcceptedOrderAsync();
        await CheckOutstandingCapacityAsync();
        await CheckCommandExceptionIsolationAsync();
        await CheckDrainAsync();
        await CheckReentrantSubmissionAsync();
        await CheckForcedShutdownAsync();
        CheckAsyncCommandsAreRejected();
    }

    private static async Task CheckConcurrentSingleOwnerAsync()
    {
        const int commandCount = 128;
        var owner = new CounterOwner();
        await using var mailbox =
            new BoundedSingleOwnerMailbox<CounterOwner>(
                owner,
                commandCount);

        var producerTasks = Enumerable.Range(0, commandCount)
            .Select(_ => Task.Run(() =>
                mailbox.TrySubmit(current =>
                {
                    var active =
                        Interlocked.Increment(ref current.Active);
                    UpdateMaximum(ref current.MaximumActive, active);
                    Thread.SpinWait(2_000);
                    Interlocked.Increment(ref current.Executed);
                    Interlocked.Decrement(ref current.Active);
                    return SingleOwnerMailboxUnit.Value;
                })))
            .ToArray();
        var submissions = await Task.WhenAll(producerTasks);

        Check.True(
            submissions.All(static item => item.IsAccepted),
            "all commands fit within the configured outstanding bound");
        await Task.WhenAll(
            submissions.Select(static item =>
                item.RequireCompletion())).WaitAsync(Timeout);
        await WaitUntilAsync(
            () => !mailbox.GetSnapshot().RunnerActive);

        var snapshot = mailbox.GetSnapshot();
        Check.Equal(
            1,
            owner.MaximumActive,
            "concurrent producers never create two active owners");
        Check.Equal(
            commandCount,
            owner.Executed,
            "every accepted concurrent command executes once");
        Check.Equal(
            commandCount,
            checked((int)snapshot.Processed),
            "processed counter records every command");
        Check.Equal(
            0,
            snapshot.Depth,
            "outstanding depth returns to zero");
        Check.True(
            !snapshot.RunnerActive,
            "on-demand runner exits when the queue becomes empty");
    }

    private static async Task CheckFifoAcceptedOrderAsync()
    {
        var owner = new OrderedOwner();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        await using var mailbox =
            new BoundedSingleOwnerMailbox<OrderedOwner>(
                owner,
                capacity: 3);

        var first = mailbox.TrySubmit(current =>
        {
            current.Order.Enqueue(1);
            firstStarted.Set();
            releaseFirst.Wait(Timeout);
            return 1;
        });
        Check.True(
            firstStarted.Wait(Timeout),
            "first FIFO command starts");
        var second = mailbox.TrySubmit(current =>
        {
            current.Order.Enqueue(2);
            return 2;
        });
        var third = mailbox.TrySubmit(current =>
        {
            current.Order.Enqueue(3);
            return 3;
        });

        Check.True(
            first.IsAccepted &&
            second.IsAccepted &&
            third.IsAccepted,
            "three FIFO commands are admitted");
        releaseFirst.Set();
        await Task.WhenAll(
            first.RequireCompletion(),
            second.RequireCompletion(),
            third.RequireCompletion()).WaitAsync(Timeout);

        Check.True(
            owner.Order.ToArray().SequenceEqual([1, 2, 3]),
            "accepted commands execute in admission order");
    }

    private static async Task CheckOutstandingCapacityAsync()
    {
        var owner = new CounterOwner();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var mailbox =
            new BoundedSingleOwnerMailbox<CounterOwner>(
                owner,
                capacity: 2);

        var active = mailbox.TrySubmit(current =>
        {
            started.Set();
            release.Wait(Timeout);
            current.Executed++;
            return 1;
        });
        Check.True(
            started.Wait(Timeout),
            "capacity fixture has one active command");
        var queued = mailbox.TrySubmit(current =>
        {
            current.Executed++;
            return 2;
        });
        var rejected = mailbox.TrySubmit(_ => 3);

        Check.True(
            active.IsAccepted && queued.IsAccepted,
            "active and queued commands consume both capacity slots");
        Check.Equal(
            (int)SingleOwnerMailboxAdmissionStatus.Overloaded,
            (int)rejected.Status,
            "capacity includes the active command");
        var saturated = mailbox.GetSnapshot();
        Check.Equal(
            2,
            saturated.Depth,
            "snapshot depth includes active and queued work");
        Check.Equal(
            1L,
            saturated.RejectedOverloaded,
            "overload rejection is counted");
        Check.Throws<SingleOwnerMailboxAdmissionException>(
            () => rejected.RequireCompletion(),
            "rejected submission has a typed admission exception");

        release.Set();
        await Task.WhenAll(
            active.RequireCompletion(),
            queued.RequireCompletion()).WaitAsync(Timeout);
    }

    private static async Task CheckCommandExceptionIsolationAsync()
    {
        var owner = new CounterOwner();
        await using var mailbox =
            new BoundedSingleOwnerMailbox<CounterOwner>(
                owner,
                capacity: 2);

        var failed = mailbox.TrySubmit<int>(
            _ => throw new InvalidOperationException(
                "synthetic command fault"));
        var healthy = mailbox.TrySubmit(current =>
        {
            current.Executed++;
            return 42;
        });

        var observed = false;
        try
        {
            await failed.RequireCompletion().WaitAsync(Timeout);
        }
        catch (InvalidOperationException)
        {
            observed = true;
        }
        Check.True(
            observed,
            "command exception completes only its caller");
        Check.Equal(
            42,
            await healthy.RequireCompletion().WaitAsync(Timeout),
            "runner continues after a command exception");

        var snapshot = mailbox.GetSnapshot();
        Check.Equal(
            2L,
            snapshot.Processed,
            "failed and healthy commands are both processed");
        Check.Equal(
            1L,
            snapshot.CommandFaults,
            "command fault is counted without a worker fault");
        Check.Equal(
            0L,
            snapshot.WorkerFaults,
            "command exception does not kill the runner");
    }

    private static async Task CheckDrainAsync()
    {
        var owner = new OrderedOwner();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var mailbox =
            new BoundedSingleOwnerMailbox<OrderedOwner>(
                owner,
                capacity: 2);

        var first = mailbox.TrySubmit(current =>
        {
            started.Set();
            release.Wait(Timeout);
            current.Order.Enqueue(1);
            return 1;
        });
        Check.True(started.Wait(Timeout), "drain fixture starts");
        var second = mailbox.TrySubmit(current =>
        {
            current.Order.Enqueue(2);
            return 2;
        });
        Check.Equal(
            (int)SingleOwnerMailboxDrainStatus.Started,
            (int)mailbox.BeginDrain(),
            "drain begins once");
        var rejected = mailbox.TrySubmit(_ => 3);
        Check.Equal(
            (int)SingleOwnerMailboxAdmissionStatus.Draining,
            (int)rejected.Status,
            "draining mailbox rejects new work");

        release.Set();
        await Task.WhenAll(
            first.RequireCompletion(),
            second.RequireCompletion()).WaitAsync(Timeout);
        await mailbox.Completion.WaitAsync(Timeout);

        Check.True(
            owner.Order.ToArray().SequenceEqual([1, 2]),
            "drain executes every previously accepted command");
        Check.Equal(
            (int)SingleOwnerMailboxState.Stopped,
            (int)mailbox.GetSnapshot().State,
            "fully drained mailbox becomes stopped");
        Check.Equal(
            (int)SingleOwnerMailboxAdmissionStatus.Stopped,
            (int)mailbox.TrySubmit(_ => 4).Status,
            "stopped mailbox has a distinct rejection");
    }

    private static async Task CheckReentrantSubmissionAsync()
    {
        var owner = new OrderedOwner();
        BoundedSingleOwnerMailbox<OrderedOwner>? mailbox = null;
        var nestedCompletion =
            new TaskCompletionSource<Task<int>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        mailbox = new BoundedSingleOwnerMailbox<OrderedOwner>(
            owner,
            capacity: 2);
        await using var ownedMailbox = mailbox;

        var outer = mailbox.TrySubmit(current =>
        {
            current.Order.Enqueue(1);
            var inline = mailbox.Invoke(
                inlineOwner =>
                {
                    inlineOwner.Order.Enqueue(2);
                    return 2;
                },
                Timeout);
            var nested = mailbox.TrySubmit(nestedOwner =>
            {
                nestedOwner.Order.Enqueue(4);
                return 4;
            });
            nestedCompletion.TrySetResult(
                nested.RequireCompletion());
            current.Order.Enqueue(3);
            return inline;
        });

        Check.Equal(
            2,
            await outer.RequireCompletion().WaitAsync(Timeout),
            "outer reentrant command completes");
        Check.Equal(
            4,
            await (await nestedCompletion.Task.WaitAsync(Timeout))
                .WaitAsync(Timeout),
            "nested command completes behind its caller");
        Check.True(
            owner.Order.ToArray().SequenceEqual([1, 2, 3, 4]),
            "inline invocation and reentrant posting preserve one owner");
    }

    private static async Task CheckForcedShutdownAsync()
    {
        var owner = new CounterOwner();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var mailbox =
            new BoundedSingleOwnerMailbox<CounterOwner>(
                owner,
                capacity: 2,
                shutdownTimeout: TimeSpan.FromMilliseconds(25));
        await using var ownedMailbox = mailbox;

        var active = mailbox.TrySubmit(current =>
        {
            started.Set();
            release.Wait(Timeout);
            current.Executed++;
            return 1;
        });
        Check.True(
            started.Wait(Timeout),
            "forced shutdown fixture starts");
        var queued = mailbox.TrySubmit(_ => 2);

        var shutdown = await mailbox.ShutdownAsync(
            TimeSpan.FromMilliseconds(25));
        Check.Equal(
            (int)SingleOwnerMailboxShutdownStatus.Forced,
            (int)shutdown,
            "shutdown returns after its finite deadline");
        Check.Equal(
            1L,
            mailbox.GetSnapshot().Abandoned,
            "forced shutdown abandons queued but not active work");

        var abandonedObserved = false;
        try
        {
            await queued.RequireCompletion().WaitAsync(Timeout);
        }
        catch (SingleOwnerMailboxStoppedException)
        {
            abandonedObserved = true;
        }
        Check.True(
            abandonedObserved,
            "abandoned accepted caller receives a typed stop exception");

        release.Set();
        Check.Equal(
            1,
            await active.RequireCompletion().WaitAsync(Timeout),
            "already active synchronous work reports its actual result");
        await WaitUntilAsync(
            () => !mailbox.GetSnapshot().RunnerActive);
    }

    private static void CheckAsyncCommandsAreRejected()
    {
        var mailbox =
            new BoundedSingleOwnerMailbox<CounterOwner>(
                new CounterOwner(),
                capacity: 1);
        Check.Throws<ArgumentException>(
            () => mailbox.TrySubmit(_ => Task.CompletedTask),
            "Task-returning owner command is rejected before admission");
        mailbox.BeginDrain();
    }

    private static void UpdateMaximum(
        ref int target,
        int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= candidate ||
                Interlocked.CompareExchange(
                    ref target,
                    candidate,
                    current) == current)
            {
                return;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Single-owner mailbox condition timed out.");
            }
            await Task.Delay(5);
        }
    }

    private sealed class CounterOwner
    {
        public int Active;

        public int Executed;

        public int MaximumActive;
    }

    private sealed class OrderedOwner
    {
        public ConcurrentQueue<int> Order { get; } = new();
    }
}
