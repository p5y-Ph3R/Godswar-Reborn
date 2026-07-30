using Godswar.Server.Application.Characters;

namespace Godswar.Server.ProtocolChecks;

internal sealed class GameHandlerCheckpointCoordinatorStub :
    ICharacterCheckpointCoordinator
{
    public Task RunAsync(
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task WaitUntilReadyAsync(
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public CharacterCheckpointEnqueueResult TryEnqueue(
        CharacterPositionCheckpoint checkpoint) =>
        new(CharacterCheckpointEnqueueStatus.Accepted, checkpoint.Revision);

    public CharacterCheckpointEnqueueResult TryEnqueue(
        CharacterVitalsCheckpoint checkpoint) =>
        new(CharacterCheckpointEnqueueStatus.Accepted, checkpoint.Revision);

    public Task<CharacterCheckpointOwnership?> AcquireAsync(
        int accountId,
        int characterId,
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<CharacterCheckpointOwnership?>(
            new(
                new CharacterCheckpointOwner(ownerId, 1),
                PositionRevision: 0,
                VitalsRevision: 0));

    public Task<CharacterCheckpointWriteResult> FlushThroughAsync(
        CharacterPositionCheckpoint checkpoint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            new CharacterCheckpointWriteResult(
                CharacterCheckpointWriteStatus.Applied,
                checkpoint.Revision));

    public Task<CharacterCheckpointWriteResult> FlushThroughAsync(
        CharacterVitalsCheckpoint checkpoint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            new CharacterCheckpointWriteResult(
                CharacterCheckpointWriteStatus.Applied,
                checkpoint.Revision));

    public Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CharacterCheckpointReleaseStatus.Released);

    public CharacterCheckpointRuntimeSnapshot GetSnapshot() =>
        new(
            CharacterCheckpointRuntimeState.Ready,
            Capacity: 1,
            PendingKeys: 0,
            ActiveWrites: 0,
            ScheduledRetries: 0,
            OldestPendingAge: TimeSpan.Zero,
            HeartbeatAge: TimeSpan.Zero,
            FailureType: null);

    public void Complete()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
