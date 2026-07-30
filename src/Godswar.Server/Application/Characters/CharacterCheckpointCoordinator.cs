using System.Threading.Channels;

namespace Godswar.Server.Application.Characters;

internal sealed partial class CharacterCheckpointCoordinator :
    ICharacterCheckpointCoordinator
{
    private readonly Channel<CheckpointKey> _queue;
    private readonly Dictionary<CheckpointKey, PendingEntry> _pending =
        [];
    private readonly SemaphoreSlim _directOperations;
    private readonly CancellationTokenSource _disposeStop = new();
    private readonly CharacterCheckpointMetrics _metrics;
    private readonly CharacterCheckpointWorkerOptions _options;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ICharacterCheckpointStore _store;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;

    private int _activeDirectOperations;
    private int _activeWrites;
    private TaskCompletionSource _directOperationsDrained =
        CompletedSignal();
    private int _disposed;
    private Exception? _failure;
    private Exception? _fatalFailure;
    private long _lastHeartbeatTimestamp;
    private Task? _runTask;
    private int _scheduledRetries;
    private CharacterCheckpointRuntimeState _state =
        CharacterCheckpointRuntimeState.Created;

    public CharacterCheckpointCoordinator(
        ICharacterCheckpointStore store,
        CharacterCheckpointWorkerOptions options,
        TimeProvider? timeProvider = null)
    {
        _store = store ??
            throw new ArgumentNullException(nameof(store));
        _options = options ??
            throw new ArgumentNullException(nameof(options));
        options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastHeartbeatTimestamp = _timeProvider.GetTimestamp();
        _directOperations = new SemaphoreSlim(
            options.DirectOperationConcurrency,
            options.DirectOperationConcurrency);
        _queue = Channel.CreateBounded<CheckpointKey>(
            new BoundedChannelOptions(options.QueueCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = options.WorkerCount == 1,
                SingleWriter = false
            });
        _metrics = new CharacterCheckpointMetrics(GetSnapshot);
    }

    public Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_runTask is not null)
            {
                throw new InvalidOperationException(
                    "The checkpoint coordinator can run only once.");
            }

            _state = CharacterCheckpointRuntimeState.Starting;
            _runTask = RunCoreAsync(cancellationToken);
            return _runTask;
        }
    }

    public Task WaitUntilReadyAsync(
        CancellationToken cancellationToken = default) =>
        _ready.Task.WaitAsync(cancellationToken);

    public CharacterCheckpointRuntimeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var oldest = _pending.Count == 0
                ? TimeSpan.Zero
                : _pending.Values
                    .Select(entry =>
                        NonNegative(now - entry.FirstEnqueuedAt))
                    .Max();
            var heartbeatAge = NonNegative(
                _timeProvider.GetElapsedTime(
                    _lastHeartbeatTimestamp,
                    _timeProvider.GetTimestamp()));
            return new CharacterCheckpointRuntimeSnapshot(
                _state,
                _options.QueueCapacity,
                _pending.Count,
                _activeWrites + _activeDirectOperations,
                _scheduledRetries,
                oldest,
                heartbeatAge,
                _failure?.GetType().Name);
        }
    }

    public void Complete()
    {
        lock (_sync)
        {
            if (_state is CharacterCheckpointRuntimeState.Stopped or
                CharacterCheckpointRuntimeState.Faulted or
                CharacterCheckpointRuntimeState.Disposed)
            {
                return;
            }
            if (_state == CharacterCheckpointRuntimeState.Created)
            {
                _state = CharacterCheckpointRuntimeState.Stopped;
                _queue.Writer.TryComplete();
                _ready.TrySetException(
                    new InvalidOperationException(
                        "The checkpoint coordinator stopped before startup."));
                return;
            }

            _state = CharacterCheckpointRuntimeState.Draining;
            TryCompleteQueueIfDrainedLocked();
        }
    }

    internal void ForceStop()
    {
        _disposeStop.Cancel();
        _queue.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Complete();
        Task? runTask;
        lock (_sync)
        {
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            var forceStopRequested =
                _disposeStop.IsCancellationRequested;
            try
            {
                await runTask.WaitAsync(
                    forceStopRequested
                        ? _options.CommandTimeout
                        : _options.ShutdownDrainTimeout,
                    _timeProvider,
                    CancellationToken.None);
            }
            catch (TimeoutException) when (!forceStopRequested)
            {
                ForceStop();
                try
                {
                    await ObserveCompletionAsync(runTask).WaitAsync(
                        _options.CommandTimeout,
                        _timeProvider,
                        CancellationToken.None);
                }
                catch (TimeoutException)
                {
                }
            }
            catch (TimeoutException)
            {
            }
            catch
            {
                // The returned run task remains the authoritative fault.
            }
        }

        ForceStop();
        Task directOperationsDrained;
        lock (_sync)
        {
            directOperationsDrained = _directOperationsDrained.Task;
        }
        try
        {
            await directOperationsDrained.WaitAsync(
                _options.CommandTimeout,
                _timeProvider,
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
        }

        var canDisposeLifetimeResources = runTask?.IsCompleted != false &&
            directOperationsDrained.IsCompletedSuccessfully;
        lock (_sync)
        {
            _state = CharacterCheckpointRuntimeState.Disposed;
            TouchHeartbeatLocked();
        }
        _queue.Writer.TryComplete();
        _ready.TrySetException(
            new ObjectDisposedException(
                nameof(CharacterCheckpointCoordinator)));
        if (canDisposeLifetimeResources)
        {
            _metrics.Dispose();
            _disposeStop.Dispose();
        }
    }

    private async Task RunCoreAsync(
        CancellationToken hostCancellation)
    {
        await Task.Yield();
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                hostCancellation,
                _disposeStop.Token);
        lock (_sync)
        {
            if (_state == CharacterCheckpointRuntimeState.Starting)
            {
                _state = CharacterCheckpointRuntimeState.Ready;
            }
            TouchHeartbeatLocked();
            if (_state == CharacterCheckpointRuntimeState.Ready)
            {
                _ready.TrySetResult();
            }
            else
            {
                _ready.TrySetException(
                    new InvalidOperationException(
                        "The checkpoint coordinator entered drain " +
                        "before becoming ready."));
                TryCompleteQueueIfDrainedLocked();
            }
        }

        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(_ => RunWorkerAsync(lifetime.Token))
            .ToArray();
        foreach (var worker in workers)
        {
            _ = worker.ContinueWith(
                static (_, state) =>
                    ((CancellationTokenSource)state!).Cancel(),
                lifetime,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        var heartbeat = RunHeartbeatAsync(lifetime.Token);
        try
        {
            await Task.WhenAll(workers);
            Exception? fatal;
            lock (_sync)
            {
                fatal = _fatalFailure;
            }
            if (fatal is not null)
            {
                throw fatal;
            }
        }
        catch (OperationCanceledException)
            when (hostCancellation.IsCancellationRequested ||
                _disposeStop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            MarkFaulted(error);
            lifetime.Cancel();
            await ObserveAllAsync(workers);
            throw;
        }
        finally
        {
            lifetime.Cancel();
            await ObserveCompletionAsync(heartbeat);
            lock (_sync)
            {
                foreach (var entry in _pending.Values)
                {
                    entry.RetryScheduled = false;
                }
                _pending.Clear();
                _activeWrites = 0;
                _scheduledRetries = 0;
                if (_state is not
                        CharacterCheckpointRuntimeState.Faulted and not
                        CharacterCheckpointRuntimeState.Disposed)
                {
                    _state = CharacterCheckpointRuntimeState.Stopped;
                }
                TouchHeartbeatLocked();
            }
        }
    }

    private async Task RunHeartbeatAsync(
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(
                       cancellationToken))
            {
                lock (_sync)
                {
                    TouchHeartbeatLocked();
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void MarkFaulted(Exception error)
    {
        lock (_sync)
        {
            _failure ??= error;
            _fatalFailure ??= error;
            if (_state != CharacterCheckpointRuntimeState.Disposed)
            {
                _state = CharacterCheckpointRuntimeState.Faulted;
            }
            TouchHeartbeatLocked();
        }
        _queue.Writer.TryComplete(error);
        _ready.TrySetException(error);
    }

    private void ReportAsynchronousFault(Exception error)
    {
        lock (_sync)
        {
            _fatalFailure ??= error;
        }
        _disposeStop.Cancel();
        _queue.Writer.TryComplete(error);
    }

    private void TouchHeartbeatLocked()
    {
        _lastHeartbeatTimestamp = _timeProvider.GetTimestamp();
    }

    private void TryCompleteQueueIfDrainedLocked()
    {
        if (_state == CharacterCheckpointRuntimeState.Draining &&
            _pending.Count == 0 &&
            _activeDirectOperations == 0 &&
            _activeWrites == 0 &&
            _scheduledRetries == 0)
        {
            _queue.Writer.TryComplete();
        }
    }

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TaskCompletionSource CompletedSignal()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.TrySetResult();
        return completion;
    }

    private TimeSpan JitteredRetryDelay(int failureCount)
    {
        var maximum = _options.RetryDelay(failureCount);
        var multiplier = 0.75d + Random.Shared.NextDouble() * 0.25d;
        return TimeSpan.FromTicks(
            Math.Max(1L, (long)(maximum.Ticks * multiplier)));
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static async Task ObserveAllAsync(
        IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            await ObserveCompletionAsync(task);
        }
    }
}
