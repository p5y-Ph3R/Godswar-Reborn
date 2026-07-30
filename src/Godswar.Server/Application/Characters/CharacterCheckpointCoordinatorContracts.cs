namespace Godswar.Server.Application.Characters;

internal enum CharacterCheckpointFacet : byte
{
    Position = 1,
    Vitals = 2
}

internal enum CharacterCheckpointEnqueueStatus : byte
{
    Accepted = 1,
    Coalesced = 2,
    IgnoredStale = 3,
    RevisionConflict = 4,
    OwnershipLost = 5,
    Saturated = 6,
    NotReady = 7
}

internal readonly record struct CharacterCheckpointEnqueueResult(
    CharacterCheckpointEnqueueStatus Status,
    long? PendingRevision)
{
    public bool Accepted =>
        Status is CharacterCheckpointEnqueueStatus.Accepted or
            CharacterCheckpointEnqueueStatus.Coalesced;
}

internal enum CharacterCheckpointRuntimeState : byte
{
    Created = 1,
    Starting = 2,
    Ready = 3,
    Draining = 4,
    Stopped = 5,
    Faulted = 6,
    Disposed = 7
}

internal readonly record struct CharacterCheckpointRuntimeSnapshot(
    CharacterCheckpointRuntimeState State,
    int Capacity,
    int PendingKeys,
    int ActiveWrites,
    int ScheduledRetries,
    TimeSpan OldestPendingAge,
    TimeSpan HeartbeatAge,
    string? FailureType)
{
    public bool IsReady =>
        State == CharacterCheckpointRuntimeState.Ready;
}

internal interface ICharacterCheckpointCoordinator :
    IAsyncDisposable
{
    Task RunAsync(CancellationToken cancellationToken = default);

    Task WaitUntilReadyAsync(
        CancellationToken cancellationToken = default);

    CharacterCheckpointEnqueueResult TryEnqueue(
        CharacterPositionCheckpoint checkpoint);

    CharacterCheckpointEnqueueResult TryEnqueue(
        CharacterVitalsCheckpoint checkpoint);

    Task<CharacterCheckpointOwnership?> AcquireAsync(
        int accountId,
        int characterId,
        Guid ownerId,
        CancellationToken cancellationToken = default);

    Task<CharacterCheckpointWriteResult> FlushThroughAsync(
        CharacterPositionCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<CharacterCheckpointWriteResult> FlushThroughAsync(
        CharacterVitalsCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner,
        CancellationToken cancellationToken = default);

    CharacterCheckpointRuntimeSnapshot GetSnapshot();

    void Complete();
}

internal sealed class CharacterCheckpointAdmissionException :
    Exception
{
    public CharacterCheckpointAdmissionException()
        : base(
            "Character checkpoint direct-operation admission " +
            "reached its finite bound.")
    {
    }
}

internal sealed class CharacterCheckpointRetryExhaustedException :
    Exception
{
    public CharacterCheckpointRetryExhaustedException(
        CharacterCheckpointFacet facet,
        Exception innerException)
        : base(
            $"The {facet.ToMetricTag()} checkpoint exceeded its " +
            "bounded retry age.",
            innerException)
    {
        Facet = facet;
    }

    public CharacterCheckpointFacet Facet { get; }
}
