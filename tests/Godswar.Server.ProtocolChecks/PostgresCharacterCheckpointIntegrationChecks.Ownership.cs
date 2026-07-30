using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterCheckpointIntegrationChecks
{
    private static async Task<CharacterCheckpointOwnership>
        AssertReplacementAndReleaseAsync(
            PostgresCharacterCheckpointStore store,
            NpgsqlDataSource dataSource,
            CheckpointFixture fixture,
            CharacterCheckpointOwnership first)
    {
        var replacement = await store.AcquireAsync(
            fixture.AccountId,
            fixture.CharacterId,
            Guid.NewGuid()) ??
            throw new InvalidOperationException(
                "Replacement checkpoint owner was not acquired.");
        Check.Equal(
            first.Owner.Generation + 1,
            replacement.Owner.Generation,
            "replacement owner advances generation once");
        Check.Equal(
            2L,
            replacement.PositionRevision,
            "replacement observes current position revision");
        Check.Equal(
            2L,
            replacement.VitalsRevision,
            "replacement observes current vitals revision");

        AssertWrite(
            CharacterCheckpointWriteStatus.OwnershipLost,
            2,
            await store.WritePositionAsync(
                new CharacterPositionCheckpoint(
                    fixture.AccountId,
                    fixture.CharacterId,
                    first.Owner,
                    CurrentMap: 8,
                    PositionX: 1f,
                    PositionZ: 2f,
                    Revision: 3)),
            "old owner cannot write position");
        AssertWrite(
            CharacterCheckpointWriteStatus.OwnershipLost,
            2,
            await store.WriteVitalsAsync(
                new CharacterVitalsCheckpoint(
                    fixture.AccountId,
                    fixture.CharacterId,
                    first.Owner,
                    CurrentHp: 1,
                    CurrentMp: 2,
                    Revision: 3)),
            "old owner cannot write vitals");

        Check.Equal(
            (int)CharacterCheckpointReleaseStatus.OwnershipLost,
            (int)await store.ReleaseAsync(
                fixture.AccountId,
                fixture.CharacterId,
                first.Owner),
            "old owner cannot release replacement owner");
        Check.Equal(
            (int)CharacterCheckpointReleaseStatus.Released,
            (int)await store.ReleaseAsync(
                fixture.AccountId,
                fixture.CharacterId,
                replacement.Owner),
            "current owner releases exactly once");
        Check.Equal(
            (int)CharacterCheckpointReleaseStatus.AlreadyReleased,
            (int)await store.ReleaseAsync(
                fixture.AccountId,
                fixture.CharacterId,
                replacement.Owner),
            "repeated release is finite and idempotent");

        AssertWrite(
            CharacterCheckpointWriteStatus.OwnershipLost,
            2,
            await store.WritePositionAsync(
                new CharacterPositionCheckpoint(
                    fixture.AccountId,
                    fixture.CharacterId,
                    replacement.Owner,
                    CurrentMap: 8,
                    PositionX: 1f,
                    PositionZ: 2f,
                    Revision: 3)),
            "released owner cannot write");

        var afterRelease = await ReadStateAsync(
            dataSource,
            fixture);
        Check.True(
            afterRelease.OwnerId is null,
            "release clears only the active owner ID");
        Check.Equal(
            replacement.Owner.Generation,
            afterRelease.OwnerGeneration,
            "release retains the monotonic owner generation");

        var reacquired = await store.AcquireAsync(
            fixture.AccountId,
            fixture.CharacterId,
            Guid.NewGuid()) ??
            throw new InvalidOperationException(
                "Checkpoint was not reacquired after release.");
        Check.Equal(
            replacement.Owner.Generation + 1,
            reacquired.Owner.Generation,
            "reacquire after release advances generation");
        return reacquired;
    }

    private static async Task AssertMissingIdentityAsync(
        PostgresCharacterCheckpointStore store,
        CheckpointFixture fixture,
        CharacterCheckpointOwnership owner)
    {
        Check.True(
            await store.AcquireAsync(
                fixture.AccountId + 1,
                fixture.CharacterId,
                Guid.NewGuid()) is null,
            "wrong account cannot acquire character");

        AssertWrite(
            CharacterCheckpointWriteStatus.CharacterNotFound,
            revision: null,
            await store.WritePositionAsync(
                new CharacterPositionCheckpoint(
                    fixture.AccountId + 1,
                    fixture.CharacterId,
                    owner.Owner,
                    CurrentMap: 1,
                    PositionX: 1,
                    PositionZ: 1,
                    Revision: 4)),
            "wrong account position write is not found");
        Check.Equal(
            (int)CharacterCheckpointReleaseStatus.CharacterNotFound,
            (int)await store.ReleaseAsync(
                fixture.AccountId + 1,
                fixture.CharacterId,
                owner.Owner),
            "wrong account release is not found");
    }
}
