namespace Godswar.Server.Networking;

internal sealed partial class BoundedReliableEgress
{
#if DEBUG
    internal void ProtocolCheckFailNextExactBatchAfterCommit() =>
        _queue.ProtocolCheckFailNextBatchAfterCommit();
#endif

    /// <summary>
    /// Synchronously owns every clear packet in order or owns none. This
    /// authority-fenced path never joins the asynchronous producer wait
    /// queue. <see cref="ExactEgressAdmissionOutcome.AdmittedTerminal"/>
    /// truthfully reports a post-ownership terminal fault so the caller can
    /// complete session teardown only after releasing its authority locks.
    /// </summary>
    public ExactEgressAdmissionOutcome TryWriteBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> clearPackets,
        out Task completion)
    {
        ArgumentNullException.ThrowIfNull(clearPackets);
        completion = Task.CompletedTask;
        if (clearPackets.Count == 0)
        {
            return Volatile.Read(ref _terminal) == 0
                ? ExactEgressAdmissionOutcome.Admitted
                : ExactEgressAdmissionOutcome.Rejected;
        }
        if (Volatile.Read(ref _terminal) != 0)
        {
            return ExactEgressAdmissionOutcome.Rejected;
        }

        long totalBytes = 0;
        var pending = new PendingWrite[clearPackets.Count];
        var entries = new BoundedByteQueueEntry<PendingWrite>[
            clearPackets.Count];
        for (var index = 0; index < clearPackets.Count; index++)
        {
            var packet = clearPackets[index];
            if (packet.Length > _options.ReliableEgressQueueBytes ||
                totalBytes >
                    _options.ReliableEgressQueueBytes - packet.Length)
            {
                return ExactEgressAdmissionOutcome.Rejected;
            }

            totalBytes += packet.Length;
            var write = new PendingWrite(packet.ToArray());
            pending[index] = write;
            entries[index] = new(write, write.Bytes.Length);
        }

        var admittedCompletion = pending.Length == 1
            ? pending[0].Completion
            : AwaitBatchCompletionAsync(pending);

        if (!_queue.TryEnqueueBatch(
                entries,
                out var postCommitError))
        {
            foreach (var write in pending)
            {
                write.SetResult();
            }
            return ExactEgressAdmissionOutcome.Rejected;
        }

        completion = admittedCompletion;
        if (postCommitError is null)
        {
            foreach (var write in pending)
            {
                write.EnsureAdmissionRecordedNonThrowing(_endpointRole);
            }
            return ExactEgressAdmissionOutcome.Admitted;
        }

        // Ownership already transferred. Seal synchronously before touching
        // any metrics or completion source; final callbacks are deferred to
        // the authority caller after it releases its locks.
        Seal(postCommitError);
        foreach (var write in pending)
        {
            write.SetExceptionNonThrowing(postCommitError);
            write.EnsureAdmissionRecordedNonThrowing(_endpointRole);
        }
        return ExactEgressAdmissionOutcome.AdmittedTerminal;
    }

    private static async Task AwaitBatchCompletionAsync(
        IReadOnlyList<PendingWrite> pending)
    {
        Exception? firstError = null;
        foreach (var write in pending)
        {
            try
            {
                await write.Completion;
            }
            catch (Exception error)
            {
                firstError ??= error;
            }
        }

        if (firstError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(firstError)
                .Throw();
        }
    }
}

internal enum ExactEgressAdmissionOutcome : byte
{
    Admitted,
    Rejected,
    AdmittedTerminal
}
