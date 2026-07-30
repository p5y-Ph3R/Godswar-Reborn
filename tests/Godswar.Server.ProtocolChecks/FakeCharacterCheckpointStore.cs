using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.ProtocolChecks;

internal sealed class FakeCharacterCheckpointStore :
    ICharacterCheckpointStore
{
    private long _generation;

    public Func<
        int,
        int,
        Guid,
        CancellationToken,
        Task<CharacterCheckpointOwnership?>>? Acquire { get; init; }

    public ConcurrentQueue<CharacterPositionCheckpoint> Positions
    {
        get;
    } = [];

    public Func<
        CharacterPositionCheckpoint,
        CancellationToken,
        Task<CharacterCheckpointWriteResult>>? PositionWrite
    {
        get;
        init;
    }

    public Func<
        int,
        int,
        CharacterCheckpointOwner,
        CancellationToken,
        Task<CharacterCheckpointReleaseStatus>>? Release
    {
        get;
        init;
    }

    public ConcurrentQueue<CharacterVitalsCheckpoint> Vitals
    {
        get;
    } = [];

    public Func<
        CharacterVitalsCheckpoint,
        CancellationToken,
        Task<CharacterCheckpointWriteResult>>? VitalsWrite
    {
        get;
        init;
    }

    public Task<CharacterCheckpointOwnership?> AcquireAsync(
        int accountId,
        int characterId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        if (Acquire is not null)
        {
            return Acquire(
                accountId,
                characterId,
                ownerId,
                cancellationToken);
        }

        var owner = new CharacterCheckpointOwner(
            ownerId,
            Interlocked.Increment(ref _generation));
        return Task.FromResult<CharacterCheckpointOwnership?>(
            new CharacterCheckpointOwnership(owner, 0, 0));
    }

    public Task<CharacterCheckpointWriteResult> WritePositionAsync(
        CharacterPositionCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        Positions.Enqueue(checkpoint);
        return PositionWrite?.Invoke(checkpoint, cancellationToken) ??
            Task.FromResult(
                new CharacterCheckpointWriteResult(
                    CharacterCheckpointWriteStatus.Applied,
                    checkpoint.Revision));
    }

    public Task<CharacterCheckpointWriteResult> WriteVitalsAsync(
        CharacterVitalsCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        Vitals.Enqueue(checkpoint);
        return VitalsWrite?.Invoke(checkpoint, cancellationToken) ??
            Task.FromResult(
                new CharacterCheckpointWriteResult(
                    CharacterCheckpointWriteStatus.Applied,
                    checkpoint.Revision));
    }

    public Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner,
        CancellationToken cancellationToken = default) =>
        Release?.Invoke(
            accountId,
            characterId,
            owner,
            cancellationToken) ??
        Task.FromResult(CharacterCheckpointReleaseStatus.Released);
}
