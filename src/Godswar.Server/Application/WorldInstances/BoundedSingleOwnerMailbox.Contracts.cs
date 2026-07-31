namespace Godswar.Server.Application.WorldInstances;

internal enum SingleOwnerMailboxState : byte
{
    Accepting = 1,
    Draining = 2,
    Stopped = 3
}

internal enum SingleOwnerMailboxAdmissionStatus : byte
{
    Accepted = 1,
    Overloaded = 2,
    Draining = 3,
    Stopped = 4
}

internal enum SingleOwnerMailboxDrainStatus : byte
{
    Started = 1,
    AlreadyDraining = 2,
    Stopped = 3
}

internal enum SingleOwnerMailboxShutdownStatus : byte
{
    Drained = 1,
    AlreadyStopped = 2,
    Forced = 3
}

/// <summary>
/// Marker returned by synchronous commands that have no application result.
/// The mailbox intentionally has no <c>Action</c> overload because an async
/// lambda can otherwise become <c>async void</c>.
/// </summary>
internal readonly record struct SingleOwnerMailboxUnit
{
    public static readonly SingleOwnerMailboxUnit Value = new();
}

internal readonly record struct SingleOwnerMailboxSubmission<TResult>(
    SingleOwnerMailboxAdmissionStatus Status,
    Task<TResult>? Completion)
{
    public bool IsAccepted =>
        Status == SingleOwnerMailboxAdmissionStatus.Accepted;

    public Task<TResult> RequireCompletion()
    {
        if (Completion is not null)
        {
            return Completion;
        }

        throw new SingleOwnerMailboxAdmissionException(Status);
    }
}

internal readonly record struct SingleOwnerMailboxSnapshot(
    SingleOwnerMailboxState State,
    int Capacity,
    int Depth,
    int Queued,
    int Active,
    int HighWaterDepth,
    bool RunnerActive,
    long Accepted,
    long Rejected,
    long RejectedOverloaded,
    long RejectedDraining,
    long RejectedStopped,
    long Processed,
    long CommandFaults,
    long WorkerFaults,
    long Abandoned);

internal sealed class SingleOwnerMailboxAdmissionException : Exception
{
    public SingleOwnerMailboxAdmissionException(
        SingleOwnerMailboxAdmissionStatus status)
        : base($"Single-owner mailbox admission was rejected: {status}.")
    {
        if (status == SingleOwnerMailboxAdmissionStatus.Accepted ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
    }

    public SingleOwnerMailboxAdmissionStatus Status { get; }
}

internal sealed class SingleOwnerMailboxStoppedException : Exception
{
    public SingleOwnerMailboxStoppedException()
        : base(
            "Accepted single-owner mailbox work was abandoned during " +
            "fail-safe shutdown.")
    {
    }
}

internal sealed class SingleOwnerMailboxWorkerException : Exception
{
    public SingleOwnerMailboxWorkerException(Exception innerException)
        : base("The single-owner mailbox runner faulted.", innerException)
    {
    }
}
