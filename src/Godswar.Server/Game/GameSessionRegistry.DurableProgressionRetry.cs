using System.Diagnostics;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const int DurableProgressionRetryCapacity = 4_096;
    private static readonly TimeSpan DurableProgressionRetryPollInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DurableProgressionMaximumRetryDelay =
        TimeSpan.FromSeconds(30);
    private readonly object _durableProgressionRetryGate = new();
    private readonly Dictionary<string, DurableProgressionRetryEntry>
        _durableProgressionRetries = [];
    private long _durableProgressionRetryHeartbeat;
    private int _durableProgressionRetryWorkerState;

    internal int DurableProgressionRetryCount
    {
        get
        {
            lock (_durableProgressionRetryGate)
            {
                return _durableProgressionRetries.Count;
            }
        }
    }

    public async Task RunDurableProgressionRetryAsync(
        CancellationToken cancellationToken)
    {
        if (_progressionIntervalSettlementCommands is null)
        {
            return;
        }

        Volatile.Write(
            ref _durableProgressionRetryWorkerState,
            (int)DurableProgressionRetryWorkerState.Running);
        TouchDurableProgressionRetryHeartbeat();
        using var timer = new PeriodicTimer(
            DurableProgressionRetryPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RetryDurableProgressionIntervalsOnceAsync(
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                TouchDurableProgressionRetryHeartbeat();
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown. The handoff is process-local, so the
            // remaining checkpoint tail is intentionally not extended.
            Volatile.Write(
                ref _durableProgressionRetryWorkerState,
                (int)DurableProgressionRetryWorkerState.Stopped);
            TouchDurableProgressionRetryHeartbeat();
        }
        catch
        {
            Volatile.Write(
                ref _durableProgressionRetryWorkerState,
                (int)DurableProgressionRetryWorkerState.Faulted);
            throw;
        }
    }

    internal DurableProgressionRetryRuntimeSnapshot
        GetDurableProgressionRetrySnapshot()
    {
        int count;
        DateTimeOffset? oldest = null;
        lock (_durableProgressionRetryGate)
        {
            count = _durableProgressionRetries.Count;
            if (count > 0)
            {
                oldest = _durableProgressionRetries.Values
                    .Min(entry => entry.EnqueuedAt);
            }
        }

        var heartbeat = Volatile.Read(
            ref _durableProgressionRetryHeartbeat);
        var heartbeatAge = heartbeat <= 0
            ? TimeSpan.MaxValue
            : Stopwatch.GetElapsedTime(heartbeat);
        return new DurableProgressionRetryRuntimeSnapshot(
            _progressionIntervalSettlementCommands is not null,
            (DurableProgressionRetryWorkerState)Volatile.Read(
                ref _durableProgressionRetryWorkerState),
            DurableProgressionRetryCapacity,
            count,
            oldest is null
                ? TimeSpan.Zero
                : DateTimeOffset.UtcNow - oldest.Value,
            heartbeatAge);
    }

    internal async Task<int> RetryDurableProgressionIntervalsOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var executor = _progressionIntervalSettlementCommands;
        if (executor is null)
        {
            return 0;
        }

        var due = SnapshotDurableProgressionRetries(
            entry => entry.NextAttemptAt <= now);
        var committed = 0;
        foreach (var entry in due)
        {
            if (!TryBindCurrentProgressionRetry(
                    entry,
                    out var session,
                    out var ownership,
                    out var envelope))
            {
                continue;
            }

            try
            {
                var result = await ExecuteDurableProgressionIntervalAsync(
                    executor,
                    envelope,
                    cancellationToken);
                EnsureRetrySucceeded(entry, result);
                RemoveDurableProgressionRetry(entry);
                if (!IsCurrentWorldOwnership(
                        session,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        ownership))
                {
                    session.Disconnect();
                }

                committed++;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PlayerOwnershipValidationException)
            {
                session.Disconnect();
            }
            catch (Exception ex)
            {
                RecordDurableProgressionRetryFailure(
                    entry,
                    now,
                    ex);
            }
        }

        return committed;
    }

    private async Task RetryDurableProgressionForCharacterAsync(
        ClientSession session,
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        var executor = _progressionIntervalSettlementCommands;
        if (executor is null)
        {
            return;
        }

        var pending = SnapshotDurableProgressionRetries(
            entry =>
                entry.Envelope.Subject.AccountId == accountId &&
                entry.Envelope.Subject.CharacterId == characterId);
        foreach (var entry in pending)
        {
            if (!IsCurrentWorldOwnership(
                    session,
                    accountId,
                    characterId,
                    ownership))
            {
                throw new PlayerOwnershipValidationException(
                    PlayerOwnershipValidationStatus.OwnershipLost);
            }

            var envelope = entry.Envelope with
            {
                Ownership = ownership
            };
            ProgressionIntervalSettlementExecutionResult result;
            try
            {
                result = await ExecuteDurableProgressionIntervalAsync(
                    executor,
                    envelope,
                    cancellationToken);
            }
            catch (PlayerOwnershipValidationException)
            {
                session.Disconnect();
                throw;
            }

            EnsureRetrySucceeded(entry, result);
            RemoveDurableProgressionRetry(entry);
            if (!IsCurrentWorldOwnership(
                    session,
                    accountId,
                    characterId,
                    ownership))
            {
                session.Disconnect();
                throw new PlayerOwnershipValidationException(
                    PlayerOwnershipValidationStatus.OwnershipLost);
            }
        }
    }

    private bool TryBindCurrentProgressionRetry(
        DurableProgressionRetryEntry entry,
        out ClientSession session,
        out PlayerOwnershipFence ownership,
        out CommandEnvelope<ProgressionIntervalSettlementCommand>
            envelope)
    {
        session = null!;
        ownership = default;
        envelope = entry.Envelope;
        var subject = entry.Envelope.Subject;
        foreach (var context in _sessions.Values)
        {
            if (context.AccountId != subject.AccountId ||
                context.CharacterId != subject.CharacterId ||
                !TryGetCurrentWorldOwnership(
                    context.Session,
                    subject.AccountId,
                    subject.CharacterId,
                    out ownership))
            {
                continue;
            }

            session = context.Session;
            envelope = entry.Envelope with
            {
                Ownership = ownership
            };
            return true;
        }

        return false;
    }

    private async Task ReleaseDurableProgressionSessionAsync(
        ClientSession session,
        CancellationToken cancellationToken)
    {
        if (_zodiacOnlineSessions.ContainsKey(session) ||
            _progressionBoostOnlineSessions.ContainsKey(session) ||
            !_durableProgressionOnlineSessions.TryGetValue(
                session,
                out var state))
        {
            return;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Pending is not null)
            {
                HandoffDurableProgressionRetry(
                    state.Pending.Envelope);
            }

            _durableProgressionOnlineSessions.TryRemove(
                new KeyValuePair<
                    ClientSession,
                    DurableProgressionOnlineSessionState>(
                    session,
                    state));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void HandoffDurableProgressionRetry(
        CommandEnvelope<ProgressionIntervalSettlementCommand> envelope)
    {
        lock (_durableProgressionRetryGate)
        {
            if (_durableProgressionRetries.ContainsKey(
                    envelope.OperationId))
            {
                return;
            }

            if (_durableProgressionRetries.Count >=
                DurableProgressionRetryCapacity)
            {
                throw new InvalidOperationException(
                    "The bounded durable progression retry handoff is full.");
            }

            _durableProgressionRetries.Add(
                envelope.OperationId,
                new DurableProgressionRetryEntry(
                    envelope,
                    DateTimeOffset.UtcNow));
        }
    }

    private DurableProgressionRetryEntry[]
        SnapshotDurableProgressionRetries(
            Func<DurableProgressionRetryEntry, bool> predicate)
    {
        lock (_durableProgressionRetryGate)
        {
            return _durableProgressionRetries.Values
                .Where(predicate)
                .ToArray();
        }
    }

    private void RemoveDurableProgressionRetry(
        DurableProgressionRetryEntry entry)
    {
        lock (_durableProgressionRetryGate)
        {
            if (_durableProgressionRetries.TryGetValue(
                    entry.Envelope.OperationId,
                    out var current) &&
                ReferenceEquals(current, entry))
            {
                _durableProgressionRetries.Remove(
                    entry.Envelope.OperationId);
            }
        }
    }

    private void RecordDurableProgressionRetryFailure(
        DurableProgressionRetryEntry entry,
        DateTimeOffset now,
        Exception exception)
    {
        int attempt;
        lock (_durableProgressionRetryGate)
        {
            if (!_durableProgressionRetries.TryGetValue(
                    entry.Envelope.OperationId,
                    out var current) ||
                !ReferenceEquals(current, entry))
            {
                return;
            }

            attempt = ++entry.AttemptCount;
            var exponent = Math.Min(attempt - 1, 5);
            var delay = TimeSpan.FromSeconds(1 << exponent);
            entry.NextAttemptAt =
                now + (delay > DurableProgressionMaximumRetryDelay
                    ? DurableProgressionMaximumRetryDelay
                    : delay);
        }

        if (attempt == 1 || (attempt & (attempt - 1)) == 0)
        {
            Console.WriteLine(
                $"[progression] deferred disconnect interval retry " +
                $"character={entry.Envelope.Subject.CharacterId} " +
                $"attempt={attempt}: {exception.Message}");
        }
    }

    private void TouchDurableProgressionRetryHeartbeat()
    {
        Volatile.Write(
            ref _durableProgressionRetryHeartbeat,
            Stopwatch.GetTimestamp());
    }

    private static void EnsureRetrySucceeded(
        DurableProgressionRetryEntry entry,
        ProgressionIntervalSettlementExecutionResult result)
    {
        if (!result.IsSuccess ||
            result.Receipt is null ||
            result.Projection is null)
        {
            throw new InvalidOperationException(
                "The deferred progression interval was rejected: " +
                result.Disposition);
        }

        if (result.Receipt.OnlineSessionId !=
                entry.Envelope.Command.OnlineSessionId ||
            result.Receipt.IntervalSequence !=
                entry.Envelope.Command.IntervalSequence ||
            result.Receipt.OnlineFromUtc !=
                entry.Envelope.Command.OnlineFromUtc ||
            result.Receipt.OnlineUntilUtc !=
                entry.Envelope.Command.OnlineUntilUtc)
        {
            throw new InvalidOperationException(
                "The deferred progression receipt did not match its envelope.");
        }
    }

    private sealed class DurableProgressionRetryEntry(
        CommandEnvelope<ProgressionIntervalSettlementCommand> envelope,
        DateTimeOffset nextAttemptAt)
    {
        public CommandEnvelope<ProgressionIntervalSettlementCommand>
            Envelope
        { get; } = envelope;
        public DateTimeOffset EnqueuedAt { get; } = nextAttemptAt;
        public DateTimeOffset NextAttemptAt { get; set; } = nextAttemptAt;
        public int AttemptCount { get; set; }
    }
}

internal enum DurableProgressionRetryWorkerState : byte
{
    NotStarted = 0,
    Running = 1,
    Stopped = 2,
    Faulted = 3,
}

internal readonly record struct DurableProgressionRetryRuntimeSnapshot(
    bool Enabled,
    DurableProgressionRetryWorkerState State,
    int Capacity,
    int QueueDepth,
    TimeSpan OldestAge,
    TimeSpan HeartbeatAge);
