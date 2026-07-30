using Godswar.Server.Application.Characters;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private Task PersistRoutineVitalsAsync(
        GameSessionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PersistRoutineVitalsAsync(
            context.AccountId,
            context.Character,
            cancellationToken);
    }

    private Task PersistRoutineVitalsAsync(
        int accountId,
        GameCharacter character,
        CancellationToken cancellationToken)
    {
        var snapshot = SnapshotVitals(accountId, character);
        if (_checkpointCoordinator is null)
        {
            return SaveLegacyVitalsCompatibilityAsync(
                snapshot,
                cancellationToken);
        }

        var checkpoint = snapshot.ToOwnedCheckpoint();
        var result = _checkpointCoordinator.TryEnqueue(checkpoint);
        if (result.Status is
            CharacterCheckpointEnqueueStatus.Accepted or
            CharacterCheckpointEnqueueStatus.Coalesced or
            CharacterCheckpointEnqueueStatus.IgnoredStale)
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            "Vitals checkpoint admission was deferred with status " +
            $"{result.Status}.");
    }

    private async Task FlushVitalsAsync(
        int accountId,
        GameCharacter character,
        CancellationToken cancellationToken)
    {
        var snapshot = SnapshotVitals(accountId, character);
        if (_checkpointCoordinator is null)
        {
            await SaveLegacyVitalsCompatibilityAsync(
                snapshot,
                cancellationToken);
            return;
        }

        var checkpoint = snapshot.ToOwnedCheckpoint();
        var result = await _checkpointCoordinator.FlushThroughAsync(
            checkpoint,
            cancellationToken);
        if (!result.Satisfies(checkpoint.Revision))
        {
            throw new InvalidOperationException(
                "Vitals checkpoint flush did not reach revision " +
                $"{checkpoint.Revision}; status={result.Status}.");
        }
    }

    private Task SaveLegacyVitalsCompatibilityAsync(
        RegistryVitalsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return Task.CompletedTask;
        }

        // Transitional compatibility for focused registry tests and storage
        // profiles that do not yet compose the shared checkpoint coordinator.
        // Production startup supplies the coordinator and does not use this
        // path.
        return _store.SaveCharacterVitalsAsync(
            snapshot.AccountId,
            snapshot.CharacterId,
            snapshot.CurrentHp,
            snapshot.CurrentMp,
            snapshot.Revision,
            cancellationToken);
    }

    private static RegistryVitalsSnapshot SnapshotVitals(
        int accountId,
        GameCharacter character)
    {
        lock (character.VitalsSync)
        {
            return new RegistryVitalsSnapshot(
                accountId,
                character.Id,
                character.CurrentHp,
                character.CurrentMp,
                character.VitalsRevision,
                character.CheckpointOwnerId,
                character.CheckpointOwnerGeneration);
        }
    }

    private readonly record struct RegistryVitalsSnapshot(
        int AccountId,
        int CharacterId,
        int CurrentHp,
        int CurrentMp,
        long Revision,
        Guid OwnerId,
        long OwnerGeneration)
    {
        public CharacterVitalsCheckpoint ToOwnedCheckpoint()
        {
            if (OwnerId == Guid.Empty || OwnerGeneration <= 0)
            {
                throw new InvalidOperationException(
                    "The character does not own a durable checkpoint lease.");
            }

            return new CharacterVitalsCheckpoint(
                AccountId,
                CharacterId,
                new CharacterCheckpointOwner(
                    OwnerId,
                    OwnerGeneration),
                CurrentHp,
                CurrentMp,
                Revision);
        }
    }
}
