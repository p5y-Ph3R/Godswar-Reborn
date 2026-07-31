using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const float SkillCastMovementTolerance = 0.05f;

    private readonly object _skillCastSync = new();
    private readonly CancellationTokenSource _skillCastLifetime = new();
    private readonly HashSet<Task> _skillCastTasks = [];
    private readonly TaskCompletionSource _skillCastStopCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PendingSkillCast? _pendingSkillCast;
    private long _nextSkillCastGeneration;
    private bool _skillCastStopped;

    private bool HasPendingSkillCast
    {
        get
        {
            lock (_skillCastSync)
            {
                return _pendingSkillCast is not null;
            }
        }
    }

    private bool IsSkillCastPending(uint skillId)
    {
        lock (_skillCastSync)
        {
            return _pendingSkillCast?.SkillId == skillId;
        }
    }

    private async Task<bool> TryBeginPendingSkillCastAsync(
        uint skillId,
        TimeSpan castTime,
        string label,
        Func<CancellationToken, Task> publishStartAsync,
        Func<CancellationToken, Task> completeAsync,
        CancellationToken cancellationToken,
        Func<bool>? additionalCompletionValidation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(publishStartAsync);
        ArgumentNullException.ThrowIfNull(completeAsync);
        if (castTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(castTime),
                castTime,
                "A pending cast cannot have a negative intonation time.");
        }

        var character = _character;
        if (character is null)
        {
            return false;
        }

        var context = new PendingSkillCastContext(
            character.Id,
            character.CurrentMap,
            character.PositionX,
            character.PositionZ,
            _registry.GetPlayerLifeRevision(_session));
        PendingSkillCast pending;
        Task lifecycleTask;
        lock (_skillCastSync)
        {
            if (_skillCastStopped ||
                _pendingSkillCast is not null)
            {
                return false;
            }

            _nextSkillCastGeneration =
                _nextSkillCastGeneration == long.MaxValue
                    ? 1
                    : _nextSkillCastGeneration + 1;
            pending = new PendingSkillCast(
                _nextSkillCastGeneration,
                skillId,
                label,
                context,
                CancellationTokenSource.CreateLinkedTokenSource(
                    _skillCastLifetime.Token,
                    cancellationToken),
                completeAsync,
                additionalCompletionValidation);
            _pendingSkillCast = pending;
            lifecycleTask = RunPendingSkillCastLifecycleAsync(
                pending,
                castTime,
                publishStartAsync,
                cancellationToken);
            pending.LifecycleTask = lifecycleTask;
            _skillCastTasks.Add(lifecycleTask);
        }

        _ = lifecycleTask.ContinueWith(
            _ => UntrackSkillCastTask(lifecycleTask),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        pending.ReleaseLifecycleStart();

        var publication = await pending.StartPublication;
        if (publication.Error is not null)
        {
            // The lifecycle is the sole owner of the linked CTS. Do not
            // expose publication failure until it has cleared its slot and
            // completed that disposal.
            await pending.LifecycleTask;
            if (publication.Error is OperationCanceledException &&
                pending.CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(publication.Error)
                .Throw();
        }

        lock (_skillCastSync)
        {
            if (pending.InterruptionClaimed ||
                _skillCastStopped)
            {
                return false;
            }

            if (!ReferenceEquals(_pendingSkillCast, pending) &&
                !pending.CompletionSucceeded)
            {
                return false;
            }
        }

        Console.WriteLine(
            $"[skill] intonation started character={character.Name} " +
            $"skill={skillId} duration={castTime.TotalSeconds:F2}s " +
            $"generation={pending.Generation} path={label}");
        return true;
    }

    private async Task RunPendingSkillCastLifecycleAsync(
        PendingSkillCast pending,
        TimeSpan castTime,
        Func<CancellationToken, Task> publishStartAsync,
        CancellationToken publicationToken)
    {
        await pending.WaitForLifecycleStartAsync();
        var cancellationToken = pending.CancellationToken;
        try
        {
            try
            {
                // Gameplay interruption owns the delay token, not reliable
                // start publication. Cancelling an admitted reliable write
                // would make the egress terminal and disconnect the client.
                await publishStartAsync(publicationToken);
                pending.CompleteStartPublication(error: null);
            }
            catch (Exception error)
            {
                pending.CompleteStartPublication(error);
                return;
            }

            lock (_skillCastSync)
            {
                if (!ReferenceEquals(_pendingSkillCast, pending) ||
                    pending.InterruptionClaimed)
                {
                    return;
                }
            }

            await Task.Delay(castTime, cancellationToken);
            await _characterStateGate.WaitAsync(cancellationToken);
            var invalidState = false;
            try
            {
                var canComplete =
                    IsPendingSkillCastContextCurrent(pending.Context) &&
                    (pending.AdditionalCompletionValidation?.Invoke() ??
                     true);
                if (!canComplete)
                {
                    invalidState = true;
                }

                // Recheck while the cast remains interruptible. A concurrent
                // movement, death, or control status can still claim the cast
                // until the final completion lock below.
                if (!invalidState &&
                    (!IsPendingSkillCastContextCurrent(pending.Context) ||
                     !(pending.AdditionalCompletionValidation?.Invoke() ??
                       true)))
                {
                    invalidState = true;
                }

                var completionClaimed = false;
                if (!invalidState)
                {
                    lock (_skillCastSync)
                    {
                        if (ReferenceEquals(
                                _pendingSkillCast,
                                pending) &&
                            !pending.InterruptionClaimed)
                        {
                            pending.CompletionClaimed = true;
                            completionClaimed = true;
                        }
                    }
                }

                if (completionClaimed)
                {
                    // The claim is the cast's linearization point. A later
                    // movement or status belongs to the next action.
                    await pending.CompleteAsync(cancellationToken);
                    lock (_skillCastSync)
                    {
                        pending.CompletionSucceeded = true;
                        if (ReferenceEquals(
                                _pendingSkillCast,
                                pending))
                        {
                            _pendingSkillCast = null;
                        }
                    }

                    Console.WriteLine(
                        $"[skill] intonation completed " +
                        $"skill={pending.SkillId} " +
                        $"generation={pending.Generation} " +
                        $"path={pending.Label}");
                }
            }
            finally
            {
                _characterStateGate.Release();
            }

            if (invalidState)
            {
                await InterruptPendingSkillCastAsync(
                    SkillCastInterruptionReason.InvalidState,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            lock (_skillCastSync)
            {
                if (ReferenceEquals(_pendingSkillCast, pending))
                {
                    _pendingSkillCast = null;
                }
            }

            Console.WriteLine(
                $"[skill] intonation failed skill={pending.SkillId} " +
                $"generation={pending.Generation} " +
                $"path={pending.Label}: {error.Message}");
        }
        finally
        {
            pending.CompleteStartPublication(
                new OperationCanceledException(
                    "The cast ended before its start publication completed.",
                    cancellationToken));
            lock (_skillCastSync)
            {
                if (ReferenceEquals(_pendingSkillCast, pending) &&
                    !pending.InterruptionClaimed)
                {
                    _pendingSkillCast = null;
                }
            }

            pending.DisposeCancellation();
        }
    }

    private async Task InterruptPendingSkillCastAsync(
        SkillCastInterruptionReason reason,
        CancellationToken cancellationToken,
        Task? notificationBarrier = null)
    {
        PendingSkillCast? pending;
        Task? committedLifecycle = null;
        lock (_skillCastSync)
        {
            pending = _pendingSkillCast;
            if (pending is null)
            {
                return;
            }

            if (pending.CompletionClaimed)
            {
                if (reason is
                    SkillCastInterruptionReason.Death or
                    SkillCastInterruptionReason.Stunned or
                    SkillCastInterruptionReason.Silenced)
                {
                    committedLifecycle = pending.LifecycleTask;
                }
                else
                {
                    // Map transition can originate inside completion itself;
                    // movement/replacement belongs to the next action.
                    return;
                }
            }
            else if (pending.InterruptionClaimed)
            {
                return;
            }
            else
            {
                // This claim occurs before the method's first await. Status,
                // death, and movement callers can therefore mutate their
                // state knowing this generation can no longer complete.
                pending.InterruptionClaimed = true;
            }
        }

        if (committedLifecycle is not null)
        {
            // Completion won the linearization race. External death/control
            // mutation waits for that authoritative effect; self transition
            // paths return above so they can never await their own task.
            await committedLifecycle;
            return;
        }

        pending.RequestCancellation();
        await Task.Yield();

        try
        {
            // Keep this generation reserved until its start publication has
            // settled. This guarantees start-before-interrupt ordering and
            // prevents a late 10171 from clearing a newer client-side cast.
            await pending.StartPublication;
            if (notificationBarrier is not null)
            {
                await notificationBarrier;
            }

            await PublishSkillCastInterruptedAsync(
                pending,
                reason,
                CancellationToken.None);
        }
        finally
        {
            lock (_skillCastSync)
            {
                if (ReferenceEquals(_pendingSkillCast, pending))
                {
                    _pendingSkillCast = null;
                }
            }
        }
    }

    private async Task PublishSkillCastInterruptedAsync(
        PendingSkillCast pending,
        SkillCastInterruptionReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _session.SendAsync(
                PacketBuilder.SkillCastInterrupt(LocalPlayerObjectId),
                cancellationToken,
                "SkillCastInterruptedSelf");
        }
        catch (Exception error)
            when (error is IOException or ObjectDisposedException)
        {
            Console.WriteLine(
                $"[skill] interruption self notification failed " +
                $"skill={pending.SkillId}: {error.Message}");
        }

        try
        {
            await _registry.BroadcastToMapAsync(
                pending.Context.MapId,
                PacketBuilder.SkillCastInterrupt(
                    WorldObjectIds.ForPlayer(
                        pending.Context.CharacterId)),
                cancellationToken,
                _session,
                "SkillCastInterruptedWorld");
        }
        catch (Exception error)
            when (error is IOException or ObjectDisposedException)
        {
            Console.WriteLine(
                $"[skill] interruption world notification failed " +
                $"skill={pending.SkillId}: {error.Message}");
        }

        Console.WriteLine(
            $"[skill] intonation interrupted " +
            $"skill={pending.SkillId} reason={reason} " +
            $"generation={pending.Generation} path={pending.Label}");
    }

    private async Task SendBlockedSkillCastNoticeAsync(
        PlayerSkillCastControl control,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.SkillCastInterrupt(LocalPlayerObjectId),
            cancellationToken,
            "SkillCastBlockedSelf");
        Console.WriteLine(
            $"[skill] cast blocked character={_character?.Name ?? "<none>"} " +
            $"control={control}");
    }

    private async Task HandleSkillCastInterruptRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Buffer.Length != 8 ||
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.Buffer.AsSpan(0, 2)) != 8 ||
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.Buffer.AsSpan(2, 2)) !=
            Opcodes.SkillCastInterrupt ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.Buffer.AsSpan(4, 4)) != LocalPlayerObjectId)
        {
            Console.WriteLine(
                $"[skill] ignored malformed interruption request " +
                $"len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.ClientRequest,
            cancellationToken);
    }

    private bool IsPendingSkillCastContextCurrent(
        PendingSkillCastContext context)
    {
        var character = _character;
        if (character is null ||
            character.Id != context.CharacterId ||
            character.CurrentHp <= 0 ||
            character.CurrentMap != context.MapId ||
            IsMapTransitionPending ||
            !_registered ||
            !_worldPresenceAnnounced ||
            !RevalidateCurrentWorldEffectOwnership(
                "pending_skill_completion") ||
            _registry.GetPlayerLifeRevision(_session) !=
                context.LifeRevision ||
            MathF.Abs(character.PositionX - context.StartX) >
                SkillCastMovementTolerance ||
            MathF.Abs(character.PositionZ - context.StartZ) >
                SkillCastMovementTolerance ||
            _registry.GetPlayerSkillCastControl(
                _session,
                DateTimeOffset.UtcNow) !=
                PlayerSkillCastControl.None ||
            !_registry.TryGetCurrentWorldSessionByCharacterId(
                _session,
                context.MapId,
                context.CharacterId,
                out var currentContext))
        {
            return false;
        }

        return ReferenceEquals(currentContext.Session, _session) &&
               ReferenceEquals(currentContext.Character, character);
    }

    private void UntrackSkillCastTask(Task task)
    {
        lock (_skillCastSync)
        {
            _skillCastTasks.Remove(task);
        }
    }

    private Task StopPendingSkillCastsAsync()
    {
        PendingSkillCast? pending;
        Task[] tasks;
        lock (_skillCastSync)
        {
            if (_skillCastStopped)
            {
                return _skillCastStopCompletion.Task;
            }

            _skillCastStopped = true;
            pending = _pendingSkillCast;
            _pendingSkillCast = null;
            tasks = _skillCastTasks.ToArray();
        }

        _ = CompleteSkillCastStopAsync(
            pending,
            tasks);
        return _skillCastStopCompletion.Task;
    }

    private async Task CompleteSkillCastStopAsync(
        PendingSkillCast? pending,
        Task[] tasks)
    {
        try
        {
            pending?.RequestCancellation();
            _skillCastLifetime.Cancel();
            if (tasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _skillCastLifetime.Dispose();
            _skillCastStopCompletion.TrySetResult();
        }
        catch (Exception error)
        {
            _skillCastStopCompletion.TrySetException(error);
        }
    }

}
