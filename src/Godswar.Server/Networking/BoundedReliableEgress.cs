namespace Godswar.Server.Networking;

/// <summary>
/// Serializes reliable writes through an item-and-byte bounded FIFO.
/// Completion of <see cref="WriteAsync"/> means the physical transport write
/// completed; reliable data is never silently dropped.
/// </summary>
internal sealed partial class BoundedReliableEgress : IAsyncDisposable
{
    private readonly NetworkEndpointRole _endpointRole;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<Exception> _terminalizeSession;
    private readonly object _reservationGate = new();
    private readonly BoundedByteQueue<PendingWrite> _queue;
    private readonly NetworkRuntimeOptions _options;
    private readonly Task _pumpTask;
    private readonly TimeProvider _timeProvider;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _write;
    private long _pendingAdmissionBytes;
    private int _pendingAdmissionItems;
    private int _terminal;
    private Exception? _terminalError;

    public BoundedReliableEgress(
        NetworkRuntimeOptions options,
        NetworkEndpointRole endpointRole,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        Action<Exception> terminalizeSession,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _terminalizeSession = terminalizeSession ??
            throw new ArgumentNullException(nameof(terminalizeSession));
        _endpointRole = endpointRole;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queue = new BoundedByteQueue<PendingWrite>(
            options.ReliableEgressQueueItems,
            options.ReliableEgressQueueBytes);
        _pumpTask = PumpAsync();
    }

    internal BoundedByteQueueSnapshot Snapshot => _queue.Snapshot();

    public async Task WriteAsync(
        ReadOnlyMemory<byte> clearBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfTerminal();

        var byteCount = clearBytes.Length;
        if (!TryReservePendingAdmission(byteCount))
        {
            throw FailQueueOverflow(deadlineExceeded: false);
        }

        PendingWrite pending;
        try
        {
            pending = new PendingWrite(clearBytes.ToArray());
            using var deadline = new CancellationTokenSource(
                _options.QueueAdmissionTimeout,
                _timeProvider);
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token,
                deadline.Token);

            try
            {
                await _queue.EnqueueAsync(
                    pending,
                    pending.Bytes.Length,
                    admission.Token);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested
                    && !_lifetime.IsCancellationRequested)
            {
                throw FailQueueOverflow(deadlineExceeded: true);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw FailQueueOverflow(deadlineExceeded: false);
            }
        }
        finally
        {
            ReleasePendingAdmission(byteCount);
        }

        pending.EnsureAdmissionRecorded(_endpointRole);

        try
        {
            await pending.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Fail(new OperationCanceledException(
                "An admitted reliable write was cancelled.",
                cancellationToken));
            throw;
        }
    }

    public void Abort(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        FinalizeTerminal(error, notifySession: false);
    }

    public async ValueTask DisposeAsync()
    {
        FinalizeTerminal(
            new ObjectDisposedException(nameof(BoundedReliableEgress)),
            notifySession: false);

        try
        {
            await _pumpTask;
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task PumpAsync()
    {
        Exception? failure = null;
        PendingWrite? active = null;
        try
        {
            while (true)
            {
                var result = await _queue.DequeueAsync(_lifetime.Token);
                if (!result.HasItem)
                {
                    return;
                }

                active = result.Item;
                active.EnsureAdmissionRecordedNonThrowing(_endpointRole);
                try
                {
                    NetworkRuntimeMetrics.RecordReliableQueueRemoved(
                        _endpointRole,
                        NetworkTrafficDirection.Outbound,
                        itemCount: 1,
                        byteCount: result.ByteCount);
                }
                catch
                {
                }

                try
                {
                    using var deadline = new CancellationTokenSource(
                        _options.ReliableWriteTimeout,
                        _timeProvider);
                    using var writeLifetime =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            _lifetime.Token,
                            deadline.Token);
                    try
                    {
                        await _write(active.Bytes, writeLifetime.Token);
                    }
                    catch (OperationCanceledException)
                        when (deadline.IsCancellationRequested
                            && !_lifetime.IsCancellationRequested)
                    {
                        NetworkRuntimeMetrics.RecordTimeout(
                            _endpointRole,
                            NetworkTimeoutStage.ReliableWrite);
                        throw new NetworkDeadlineException(
                            NetworkTimeoutStage.ReliableWrite);
                    }

                    active.SetResult();
                    active = null;
                }
                catch (Exception ex)
                {
                    active?.SetExceptionNonThrowing(ex);
                    active = null;
                    failure = ex;
                    Fail(ex);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
            failure ??= new OperationCanceledException(
                "Reliable egress stopped.");
        }
        catch (Exception ex)
        {
            failure = ex;
            active?.SetExceptionNonThrowing(ex);
            active = null;
            Fail(ex);
        }
        finally
        {
            var terminalError = failure ??
                Volatile.Read(ref _terminalError) ??
                new ObjectDisposedException(nameof(BoundedReliableEgress));
            active?.SetExceptionNonThrowing(terminalError);
            active = null;
            while (_queue.TryTakeTerminalEntry(out var entry))
            {
                // Completion ownership is the first obligation. Metrics and
                // admission diagnostics are deliberately best effort after
                // every removed write has a terminal result.
                entry.Item.SetExceptionNonThrowing(terminalError);
                entry.Item.EnsureAdmissionRecordedNonThrowing(_endpointRole);
                try
                {
                    NetworkRuntimeMetrics.RecordReliableQueueRemoved(
                        _endpointRole,
                        NetworkTrafficDirection.Outbound,
                        itemCount: 1,
                        byteCount: entry.ByteCount);
                }
                catch
                {
                }
            }
        }
    }

    private void Fail(Exception error)
    {
        FinalizeTerminal(error, notifySession: true);
    }

    private void Seal(Exception error)
    {
        Interlocked.CompareExchange(
            ref _terminalError,
            error,
            comparand: null);
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) == 0)
        {
            _queue.Seal(error);
        }
    }

    private void FinalizeTerminal(
        Exception error,
        bool notifySession)
    {
        Seal(error);
        while (true)
        {
            var observed = Volatile.Read(ref _terminal);
            if (observed == 2)
            {
                return;
            }
            if (Interlocked.CompareExchange(
                    ref _terminal,
                    2,
                    observed) == observed)
            {
                break;
            }
        }

        try
        {
            _queue.Complete(error);
        }
        catch
        {
        }
        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // Cancellation callbacks are untrusted teardown observers.
        }
        try
        {
            if (notifySession)
            {
                _terminalizeSession(error);
            }
        }
        catch
        {
        }
    }

    private ReliableQueueOverflowException FailQueueOverflow(
        bool deadlineExceeded)
    {
        var error = new ReliableQueueOverflowException();
        if (deadlineExceeded)
        {
            NetworkRuntimeMetrics.RecordTimeout(
                _endpointRole,
                NetworkTimeoutStage.QueueAdmission);
        }

        NetworkRuntimeMetrics.RecordReliableQueueOverflow(
            _endpointRole,
            NetworkTrafficDirection.Outbound);
        Fail(error);
        return error;
    }

    private bool TryReservePendingAdmission(int byteCount)
    {
        if (byteCount < 0
            || byteCount > _options.ReliableEgressQueueBytes
            || byteCount > _options.ReliableEgressPendingBytes)
        {
            return false;
        }

        lock (_reservationGate)
        {
            if (_pendingAdmissionItems
                    >= _options.ReliableEgressPendingItems
                || _pendingAdmissionBytes
                    > _options.ReliableEgressPendingBytes - byteCount)
            {
                return false;
            }

            _pendingAdmissionItems++;
            _pendingAdmissionBytes += byteCount;
            return true;
        }
    }

    private void ReleasePendingAdmission(int byteCount)
    {
        lock (_reservationGate)
        {
            _pendingAdmissionItems--;
            _pendingAdmissionBytes -= byteCount;
        }
    }

    private void ThrowIfTerminal()
    {
        if (Volatile.Read(ref _terminal) != 0)
        {
            throw new ObjectDisposedException(nameof(BoundedReliableEgress));
        }
    }

    private sealed class PendingWrite(byte[] bytes)
    {
        private readonly TaskCompletionSource _admissionRecorded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _admissionRecordingStarted;

        public Task AdmissionRecorded => _admissionRecorded.Task;

        public byte[] Bytes { get; } = bytes;

        public Task Completion => _completion.Task;

        public void EnsureAdmissionRecorded(
            NetworkEndpointRole endpointRole)
        {
            if (Interlocked.Exchange(ref _admissionRecordingStarted, 1) != 0)
            {
                return;
            }

            try
            {
                NetworkRuntimeMetrics.RecordReliableQueueEnqueued(
                    endpointRole,
                    NetworkTrafficDirection.Outbound,
                    Bytes.Length);
            }
            finally
            {
                _admissionRecorded.TrySetResult();
            }
        }

        public void EnsureAdmissionRecordedNonThrowing(
            NetworkEndpointRole endpointRole)
        {
            try
            {
                EnsureAdmissionRecorded(endpointRole);
            }
            catch
            {
            }
        }

        public void SetResult() => _completion.TrySetResult();

        public void SetException(Exception error) =>
            _completion.TrySetException(error);

        public void SetExceptionNonThrowing(Exception error)
        {
            try
            {
                _completion.TrySetException(error);
            }
            catch
            {
            }
        }
    }
}
