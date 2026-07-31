namespace Godswar.Server.Application.WorldInstances;

internal static class SingleOwnerMailboxExecutionContext
{
    [ThreadStatic]
    public static object? Current;
}

/// <summary>
/// Executes bounded work against one logical owner, one command at a time.
/// Producers may be concurrent. A runner is created only while work exists
/// and exits as soon as the queue becomes empty.
/// </summary>
/// <remarks>
/// Commands must be finite, synchronous, in-memory work. They must not block,
/// await, perform persistence or network I/O, or synchronously wait for a
/// reentrant submission. Callers may post reentrantly; accepted nested work
/// runs in FIFO order after the current command.
/// </remarks>
internal sealed class BoundedSingleOwnerMailbox<TOwner> : IAsyncDisposable
    where TOwner : class
{
    private static readonly TimeSpan DefaultShutdownTimeout =
        TimeSpan.FromSeconds(5);

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly TOwner _owner;
    private readonly Queue<IWorkItem> _queue = [];
    private readonly TimeSpan _shutdownTimeout;
    private readonly TaskCompletionSource _stopped =
        NewSignal();

    private int _active;
    private long _accepted;
    private long _abandoned;
    private long _commandFaults;
    private int _disposeStarted;
    private int _highWaterDepth;
    private int _outstanding;
    private long _processed;
    private long _rejected;
    private long _rejectedDraining;
    private long _rejectedOverloaded;
    private long _rejectedStopped;
    private bool _runnerActive;
    private SingleOwnerMailboxState _state =
        SingleOwnerMailboxState.Accepting;
    private long _workerFaults;

    public BoundedSingleOwnerMailbox(
        TOwner owner,
        int capacity,
        TimeSpan? shutdownTimeout = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (capacity is <= 0 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "Mailbox capacity must be between 1 and 65,536.");
        }

        var resolvedTimeout =
            shutdownTimeout ?? DefaultShutdownTimeout;
        if (resolvedTimeout < TimeSpan.FromMilliseconds(10) ||
            resolvedTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(shutdownTimeout),
                resolvedTimeout,
                "Shutdown timeout must be between 10 ms and 2 minutes.");
        }

        _capacity = capacity;
        _shutdownTimeout = resolvedTimeout;
    }

    public Task Completion => _stopped.Task;

    public SingleOwnerMailboxSubmission<TResult> TrySubmit<TResult>(
        Func<TOwner, TResult> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RejectTaskLikeResult<TResult>();

        WorkItem<TResult>? item = null;
        SingleOwnerMailboxAdmissionStatus status;
        lock (_gate)
        {
            status = AdmissionStatusLocked();
            if (status == SingleOwnerMailboxAdmissionStatus.Accepted)
            {
                item = new WorkItem<TResult>(command);
                _queue.Enqueue(item);
                _outstanding++;
                _accepted = IncrementSaturated(_accepted);
                _highWaterDepth = Math.Max(
                    _highWaterDepth,
                    _outstanding);
                EnsureRunnerLocked();
            }
            else
            {
                RecordRejectionLocked(status);
            }
        }

        return new SingleOwnerMailboxSubmission<TResult>(
            status,
            item?.Completion);
    }

    /// <summary>
    /// Transitional synchronous adapter for existing in-process APIs.
    /// Calls from this mailbox's owner execute inline. Calls from outside the
    /// owner reserve normal capacity and wait to a finite deadline.
    /// </summary>
    /// <remarks>
    /// Cancellation or timeout stops only the caller's wait after admission;
    /// accepted authoritative work remains owned by the mailbox. A command
    /// must therefore remain safe if its caller stops waiting.
    /// </remarks>
    public TResult Invoke<TResult>(
        Func<TOwner, TResult> command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RejectTaskLikeResult<TResult>();
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var executingMailbox =
            SingleOwnerMailboxExecutionContext.Current;
        if (ReferenceEquals(executingMailbox, this))
        {
            lock (_gate)
            {
                if (_state == SingleOwnerMailboxState.Stopped)
                {
                    throw new SingleOwnerMailboxAdmissionException(
                        SingleOwnerMailboxAdmissionStatus.Stopped);
                }
            }

            return command(_owner);
        }
        if (executingMailbox is not null)
        {
            throw new InvalidOperationException(
                "Synchronous cross-mailbox waits from an owner are " +
                "forbidden because they can deadlock two owners.");
        }

        var submission = TrySubmit(command);
        return submission.RequireCompletion()
            .WaitAsync(timeout, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    public SingleOwnerMailboxDrainStatus BeginDrain()
    {
        lock (_gate)
        {
            if (_state == SingleOwnerMailboxState.Stopped)
            {
                return SingleOwnerMailboxDrainStatus.Stopped;
            }
            if (_state == SingleOwnerMailboxState.Draining)
            {
                return SingleOwnerMailboxDrainStatus.AlreadyDraining;
            }

            _state = SingleOwnerMailboxState.Draining;
            if (_outstanding == 0 && !_runnerActive)
            {
                MarkStoppedLocked();
            }
            return SingleOwnerMailboxDrainStatus.Started;
        }
    }

    public async Task<SingleOwnerMailboxShutdownStatus> ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var drain = BeginDrain();
        if (drain == SingleOwnerMailboxDrainStatus.Stopped)
        {
            return SingleOwnerMailboxShutdownStatus.AlreadyStopped;
        }

        try
        {
            await _stopped.Task.WaitAsync(
                timeout,
                cancellationToken);
            return SingleOwnerMailboxShutdownStatus.Drained;
        }
        catch (TimeoutException)
        {
            ForceStop();
            return SingleOwnerMailboxShutdownStatus.Forced;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            ForceStop();
            throw;
        }
    }

    public SingleOwnerMailboxSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new SingleOwnerMailboxSnapshot(
                _state,
                _capacity,
                _outstanding,
                _queue.Count,
                _active,
                _highWaterDepth,
                _runnerActive,
                _accepted,
                _rejected,
                _rejectedOverloaded,
                _rejectedDraining,
                _rejectedStopped,
                _processed,
                _commandFaults,
                _workerFaults,
                _abandoned);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _stopped.Task;
            return;
        }

        await ShutdownAsync(
            _shutdownTimeout,
            CancellationToken.None);
    }

    private SingleOwnerMailboxAdmissionStatus
        AdmissionStatusLocked()
    {
        if (_state == SingleOwnerMailboxState.Draining)
        {
            return SingleOwnerMailboxAdmissionStatus.Draining;
        }
        if (_state == SingleOwnerMailboxState.Stopped)
        {
            return SingleOwnerMailboxAdmissionStatus.Stopped;
        }
        return _outstanding >= _capacity
            ? SingleOwnerMailboxAdmissionStatus.Overloaded
            : SingleOwnerMailboxAdmissionStatus.Accepted;
    }

    private void EnsureRunnerLocked()
    {
        if (_runnerActive)
        {
            return;
        }

        _runnerActive = true;
        _ = Task.Run(RunWorker);
    }

    private void RunWorker()
    {
        IWorkItem? current = null;
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _runnerActive = false;
                        if (_state ==
                                SingleOwnerMailboxState.Draining &&
                            _outstanding == 0)
                        {
                            MarkStoppedLocked();
                        }
                        return;
                    }
                    if (_active != 0)
                    {
                        throw new InvalidOperationException(
                            "Single-owner mailbox active count is corrupt.");
                    }

                    current = _queue.Dequeue();
                    _active = 1;
                }

                var commandFaulted = false;
                Exception? commandError = null;
                var previousExecutionContext =
                    SingleOwnerMailboxExecutionContext.Current;
                SingleOwnerMailboxExecutionContext.Current = this;
                try
                {
                    current.Execute(_owner);
                }
                catch (Exception error)
                {
                    commandFaulted = true;
                    commandError = error;
                }
                finally
                {
                    SingleOwnerMailboxExecutionContext.Current =
                        previousExecutionContext;
                }

                lock (_gate)
                {
                    CompleteActiveLocked(commandFaulted);
                }
                if (commandError is null)
                {
                    current.Complete();
                }
                else
                {
                    current.Fail(commandError);
                }
                current = null;
            }
        }
        catch (Exception error)
        {
            StopForWorkerFault(current, error);
        }
    }

    private void CompleteActiveLocked(bool commandFaulted)
    {
        if (_active != 1 || _outstanding <= 0)
        {
            throw new InvalidOperationException(
                "Single-owner mailbox completion accounting is corrupt.");
        }

        _active = 0;
        _outstanding--;
        _processed = IncrementSaturated(_processed);
        if (commandFaulted)
        {
            _commandFaults = IncrementSaturated(
                _commandFaults);
        }

    }

    private void StopForWorkerFault(
        IWorkItem? current,
        Exception error)
    {
        IWorkItem[] abandoned;
        var workerError =
            new SingleOwnerMailboxWorkerException(error);
        lock (_gate)
        {
            _workerFaults = IncrementSaturated(_workerFaults);
            if (current is not null && _active != 0)
            {
                _active = 0;
                _outstanding--;
                _processed = IncrementSaturated(_processed);
            }

            abandoned = AbandonQueuedLocked();
            _runnerActive = false;
            MarkStoppedLocked();
        }

        current?.Fail(workerError);
        FailAbandoned(abandoned, workerError);
    }

    private void ForceStop()
    {
        IWorkItem[] abandoned;
        lock (_gate)
        {
            if (_state == SingleOwnerMailboxState.Stopped)
            {
                return;
            }

            abandoned = AbandonQueuedLocked();
            MarkStoppedLocked();
        }

        FailAbandoned(
            abandoned,
            new SingleOwnerMailboxStoppedException());
    }

    private IWorkItem[] AbandonQueuedLocked()
    {
        if (_queue.Count == 0)
        {
            return [];
        }

        var abandoned = _queue.ToArray();
        _queue.Clear();
        _outstanding -= abandoned.Length;
        _abandoned = AddSaturated(
            _abandoned,
            abandoned.Length);
        return abandoned;
    }

    private void MarkStoppedLocked()
    {
        _state = SingleOwnerMailboxState.Stopped;
        _stopped.TrySetResult();
    }

    private void RecordRejectionLocked(
        SingleOwnerMailboxAdmissionStatus status)
    {
        _rejected = IncrementSaturated(_rejected);
        switch (status)
        {
            case SingleOwnerMailboxAdmissionStatus.Overloaded:
                _rejectedOverloaded =
                    IncrementSaturated(_rejectedOverloaded);
                break;
            case SingleOwnerMailboxAdmissionStatus.Draining:
                _rejectedDraining =
                    IncrementSaturated(_rejectedDraining);
                break;
            case SingleOwnerMailboxAdmissionStatus.Stopped:
                _rejectedStopped =
                    IncrementSaturated(_rejectedStopped);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    private static void FailAbandoned(
        IEnumerable<IWorkItem> abandoned,
        Exception error)
    {
        foreach (var item in abandoned)
        {
            item.Fail(error);
        }
    }

    private static void RejectTaskLikeResult<TResult>()
    {
        var resultType = typeof(TResult);
        if (typeof(Task).IsAssignableFrom(resultType) ||
            resultType == typeof(ValueTask) ||
            resultType.IsGenericType &&
            resultType.GetGenericTypeDefinition() ==
                typeof(ValueTask<>))
        {
            throw new ArgumentException(
                "Mailbox commands must return a synchronous result; " +
                "Task and ValueTask results are forbidden.");
        }
    }

    private static long IncrementSaturated(long value) =>
        value == long.MaxValue ? value : value + 1;

    private static long AddSaturated(long value, int addend) =>
        value >= long.MaxValue - addend
            ? long.MaxValue
            : value + addend;

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private interface IWorkItem
    {
        void Execute(TOwner owner);

        void Complete();

        void Fail(Exception error);
    }

    private sealed class WorkItem<TResult>(
        Func<TOwner, TResult> command) : IWorkItem
    {
        private readonly TaskCompletionSource<TResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TResult _result = default!;

        public Task<TResult> Completion => _completion.Task;

        public void Execute(TOwner owner) =>
            _result = command(owner);

        public void Complete() =>
            _completion.TrySetResult(_result);

        public void Fail(Exception error) =>
            _completion.TrySetException(error);
    }
}
