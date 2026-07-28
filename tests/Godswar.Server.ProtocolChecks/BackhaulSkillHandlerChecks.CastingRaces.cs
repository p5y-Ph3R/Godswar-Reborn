using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private static readonly MethodInfo TryBeginPendingSkillCastMethod =
        typeof(GameClientHandler).GetMethod(
            "TryBeginPendingSkillCastAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.TryBeginPendingSkillCastAsync was not found.");

    public static async Task RunCastingLifecycleRacesAsync()
    {
        await CheckInterruptionDuringStartPublicationAsync();
        await CheckShutdownDuringStartPublicationAsync();
        await CheckCommittedCompletionOrderingAsync();
    }

    private static async Task
        CheckInterruptionDuringStartPublicationAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "StartPublicationInterruptRace");
        var publicationEntered = NewSignal();
        var releasePublication = NewSignal();
        var publicationTokenCancelled = 0;
        var replacementPublications = 0;
        var castPacket = CreateSkillCastPacket(
            BackhaulSkillCatalog.CitySkillId,
            fixture.Character.PositionX,
            fixture.Character.PositionZ,
            targetX: 0f,
            targetZ: 0f);

        var begin = InvokeTryBeginPendingSkillCastAsync(
            fixture.Handler,
            async token =>
            {
                using var registration = token.Register(
                    () => Interlocked.Exchange(
                        ref publicationTokenCancelled,
                        1));
                await fixture.Socket.Session.SendAsync(
                    castPacket.Buffer,
                    token,
                    "CastRaceStart");
                publicationEntered.TrySetResult();
                await releasePublication.Task.WaitAsync(token);
            },
            CancellationToken.None);

        await publicationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        var start = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            Opcodes.SkillCast,
            ReadUInt16(start, 2),
            "race fixture admits the cast start before interruption");

        var interruption = InvokeInterruptionSinkAsync(
            fixture.Handler,
            SkillCastInterruptionReason.ClientRequest,
            CancellationToken.None);

        // InterruptPendingSkillCastAsync claims synchronously before its
        // first yield, but the old generation remains reserved until its
        // start and native interruption have been ordered.
        Check.True(
            HasPendingSkillCast(fixture.Handler),
            "interruption retains the generation during publication");
        await Task.Delay(50);
        Check.True(
            !interruption.IsCompleted,
            "interruption waits for in-flight start publication");
        Check.Equal(
            0,
            Volatile.Read(ref publicationTokenCancelled),
            "gameplay interruption does not cancel reliable publication");
        Check.Equal(
            0,
            fixture.Socket.Available,
            "native interruption cannot overtake cast start publication");

        var replacement = await InvokeTryBeginPendingSkillCastAsync(
            fixture.Handler,
            _ =>
            {
                Interlocked.Increment(ref replacementPublications);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        Check.True(
            !replacement,
            "new cast is rejected until old interruption is sent");
        Check.Equal(
            0,
            Volatile.Read(ref replacementPublications),
            "rejected replacement publishes no client visual");

        releasePublication.TrySetResult();
        Check.True(
            !await begin,
            "interrupted publication never reports an active cast");
        await interruption;

        var interrupted = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            "0800BB2748140000",
            Convert.ToHexString(interrupted),
            "settled start is followed by exactly one native interruption");
        Check.True(
            !HasPendingSkillCast(fixture.Handler),
            "interruption releases its generation after notification");

        await fixture.Socket.Session.SendAsync(
            CreateControlPacket(Opcodes.Ping).Buffer,
            CancellationToken.None,
            "CastRaceEgressProbe");
        var probe = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            Opcodes.Ping,
            ReadUInt16(probe, 2),
            "gameplay cancellation leaves reliable egress usable");
    }

    private static async Task
        CheckShutdownDuringStartPublicationAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "StartPublicationShutdownRace");
        using var callerCancellation = new CancellationTokenSource();
        var publicationEntered = NewSignal();
        var releasePublication = NewSignal();

        var begin = InvokeTryBeginPendingSkillCastAsync(
            fixture.Handler,
            async token =>
            {
                publicationEntered.TrySetResult();
                await releasePublication.Task;
                token.ThrowIfCancellationRequested();
            },
            callerCancellation.Token);
        await publicationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        var firstStop = InvokeStopPendingSkillCastsAsync(
            fixture.Handler);
        var secondStop = InvokeStopPendingSkillCastsAsync(
            fixture.Handler);
        Check.True(
            ReferenceEquals(firstStop, secondStop),
            "concurrent shutdown callers share one cleanup task");
        Check.True(
            !firstStop.IsCompleted,
            "shutdown tracks an in-flight start publication lifecycle");
        Check.True(
            !HasPendingSkillCast(fixture.Handler),
            "shutdown synchronously removes the pending generation");

        callerCancellation.Cancel();
        releasePublication.TrySetResult();
        Check.True(
            !await begin,
            "linked caller cancellation ends publication without a cast");
        await Task.WhenAll(firstStop, secondStop);
        await InvokeStopPendingSkillCastsAsync(fixture.Handler);

        Check.True(
            !HasPendingSkillCast(fixture.Handler),
            "repeated shutdown leaves no stale pending cast");
        await fixture.Socket.Session.SendAsync(
            CreateControlPacket(Opcodes.Ping).Buffer,
            CancellationToken.None,
            "CastShutdownEgressProbe");
        var probe = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            Opcodes.Ping,
            ReadUInt16(probe, 2),
            "linked cast cancellation does not terminate egress");
    }

    private static async Task CheckCommittedCompletionOrderingAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "CommittedCompletionRace");
        var completionEntered = NewSignal();
        var releaseCompletion = NewSignal();

        var begin = InvokeTryBeginPendingSkillCastAsync(
            fixture.Handler,
            _ => Task.CompletedTask,
            CancellationToken.None,
            castTime: TimeSpan.Zero,
            completeAsync: async _ =>
            {
                completionEntered.TrySetResult();
                await releaseCompletion.Task;
            });
        await completionEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Check.True(
            await begin,
            "zero-time cast reports its committed completion");

        var death = InvokeInterruptionSinkAsync(
            fixture.Handler,
            SkillCastInterruptionReason.Death,
            CancellationToken.None);
        Check.True(
            !death.IsCompleted,
            "external death waits for an already committed completion");

        var selfTransition = InvokeInterruptionSinkAsync(
            fixture.Handler,
            SkillCastInterruptionReason.MapTransition,
            CancellationToken.None);
        await selfTransition.WaitAsync(TimeSpan.FromSeconds(1));
        Check.True(
            selfTransition.IsCompletedSuccessfully,
            "completion-owned map transition never self-awaits");

        releaseCompletion.TrySetResult();
        await death.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(
            !HasPendingSkillCast(fixture.Handler),
            "committed completion releases its generation");
        Check.Equal(
            0,
            fixture.Socket.Available,
            "won completion emits no stale interruption notice");
    }

    private static Task<bool> InvokeTryBeginPendingSkillCastAsync(
        GameClientHandler handler,
        Func<CancellationToken, Task> publishStartAsync,
        CancellationToken cancellationToken,
        TimeSpan? castTime = null,
        Func<CancellationToken, Task>? completeAsync = null) =>
        TryBeginPendingSkillCastMethod.Invoke(
            handler,
            [
                BackhaulSkillCatalog.CitySkillId,
                castTime ?? TimeSpan.FromSeconds(30),
                "race-test",
                publishStartAsync,
                completeAsync ??
                    new Func<CancellationToken, Task>(
                        _ => Task.CompletedTask),
                cancellationToken,
                null
            ]) as Task<bool>
        ?? throw new InvalidOperationException(
            "TryBeginPendingSkillCastAsync returned no task.");

    private static Task InvokeStopPendingSkillCastsAsync(
        GameClientHandler handler) =>
        StopPendingSkillCastsMethod.Invoke(
            handler,
            null) as Task
        ?? throw new InvalidOperationException(
            "StopPendingSkillCastsAsync returned no task.");

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
