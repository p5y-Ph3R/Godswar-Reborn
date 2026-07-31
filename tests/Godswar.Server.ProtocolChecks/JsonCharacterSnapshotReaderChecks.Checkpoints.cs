using Godswar.Server.Application.Characters;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class JsonCharacterSnapshotReaderChecks
{
    private static async Task
        AssertLocalCheckpointCompatibilityAsync()
    {
        await WithStoreAsync(async (_, store) =>
        {
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync(
                "json-checkpoint-owner",
                "local-test");
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "JsonCheckpoint",
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 1,
                    Level = 10,
                    MaxHp = 1_000,
                    MaxMp = 200,
                    CurrentHp = 900,
                    CurrentMp = 150
                });
            var checkpoints = (ICharacterCheckpointStore)store;
            var first = await checkpoints.AcquireAsync(
                account.Id,
                character.Id,
                Guid.NewGuid()) ??
                throw new InvalidOperationException(
                    "Local checkpoint owner was not acquired.");

            var position = new CharacterPositionCheckpoint(
                account.Id,
                character.Id,
                first.Owner,
                CurrentMap: 7,
                PositionX: 12.5f,
                PositionZ: -8.25f,
                Revision: 1);
            Check.True(
                (await checkpoints.WritePositionAsync(position))
                    .Satisfies(1),
                "local position checkpoint applies");
            Check.Equal(
                (int)CharacterCheckpointWriteStatus.AlreadyApplied,
                (int)(await checkpoints.WritePositionAsync(position))
                    .Status,
                "local position checkpoint retry is idempotent");
            Check.Equal(
                (int)CharacterCheckpointWriteStatus.RevisionConflict,
                (int)(await checkpoints.WritePositionAsync(
                    position with { PositionX = 13.5f })).Status,
                "local position checkpoint rejects a conflicting revision");
            var vitals = new CharacterVitalsCheckpoint(
                account.Id,
                character.Id,
                first.Owner,
                CurrentHp: 777,
                CurrentMp: 123,
                Revision: 1);
            Check.True(
                (await checkpoints.WriteVitalsAsync(vitals))
                    .Satisfies(1),
                "local vitals checkpoint applies");
            Check.Equal(
                (int)CharacterCheckpointWriteStatus.AlreadyApplied,
                (int)(await checkpoints.WriteVitalsAsync(vitals)).Status,
                "local vitals checkpoint retry is idempotent");

            var reacquired = await checkpoints.AcquireAsync(
                    account.Id,
                    character.Id,
                    first.Owner.OwnerId) ??
                throw new InvalidOperationException(
                    "Local checkpoint owner was not reacquired.");
            Check.Equal(
                first.Owner.Generation,
                reacquired.Owner.Generation,
                "local same-owner reacquisition is idempotent");
            Check.Equal(
                1L,
                reacquired.PositionRevision,
                "local reacquisition reads the persisted position revision");
            Check.Equal(
                1L,
                reacquired.VitalsRevision,
                "local reacquisition reads the persisted vitals revision");

            var replacement = await checkpoints.AcquireAsync(
                account.Id,
                character.Id,
                Guid.NewGuid()) ??
                throw new InvalidOperationException(
                    "Replacement local owner was not acquired.");
            Check.Equal(
                first.Owner.Generation + 1,
                replacement.Owner.Generation,
                "local replacement advances the owner generation");
            Check.Equal(
                (int)CharacterCheckpointWriteStatus.OwnershipLost,
                (int)(await checkpoints.WritePositionAsync(
                    position with { Revision = 2 })).Status,
                "replaced local owner is fenced");
            Check.Equal(
                (int)CharacterCheckpointReleaseStatus.OwnershipLost,
                (int)await checkpoints.ReleaseAsync(
                    account.Id,
                    character.Id,
                    first.Owner),
                "replaced local owner cannot release its successor");
            Check.Equal(
                (int)CharacterCheckpointReleaseStatus.Released,
                (int)await checkpoints.ReleaseAsync(
                    account.Id,
                    character.Id,
                    replacement.Owner),
                "local owner releases cleanly");

            var postReleaseOwner = await checkpoints.AcquireAsync(
                    account.Id,
                    character.Id,
                    replacement.Owner.OwnerId) ??
                throw new InvalidOperationException(
                    "Released local owner was not reacquired.");
            Check.Equal(
                replacement.Owner.Generation + 1,
                postReleaseOwner.Owner.Generation,
                "local post-release reacquisition advances generation");
            Check.Equal(
                (int)CharacterCheckpointWriteStatus.OwnershipLost,
                (int)(await checkpoints.WritePositionAsync(
                    position with
                    {
                        Owner = replacement.Owner,
                        Revision = 2
                    })).Status,
                "released local owner remains fenced after reacquisition");
            Check.Equal(
                (int)CharacterCheckpointReleaseStatus.Released,
                (int)await checkpoints.ReleaseAsync(
                    account.Id,
                    character.Id,
                    postReleaseOwner.Owner),
                "reacquired local owner releases cleanly");

            var snapshot = await store.ReadAsync(account.Id);
            var persisted = snapshot.Character ??
                throw new InvalidOperationException(
                    "Local checkpoint character disappeared.");
            Check.Equal(
                1L,
                persisted.Location.PositionRevision,
                "local checkpoint store persists position revision");
            Check.Equal(
                7,
                (int)persisted.Location.CurrentMap,
                "local checkpoint store persists position values");
            Check.Equal(
                1L,
                persisted.Vitals.Revision,
                "local checkpoint store persists vitals revision");
            Check.Equal(
                777,
                persisted.CalculatedStats.CurrentHp,
                "local checkpoint store persists vitals values");
        });
    }
}
