namespace Godswar.Server.Application.Characters;

internal interface ICharacterCheckpointStore
{
    Task<CharacterCheckpointOwnership?> AcquireAsync(
        int accountId,
        int characterId,
        Guid ownerId,
        CancellationToken cancellationToken = default);

    Task<CharacterCheckpointWriteResult> WritePositionAsync(
        CharacterPositionCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<CharacterCheckpointWriteResult> WriteVitalsAsync(
        CharacterVitalsCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner,
        CancellationToken cancellationToken = default);
}
