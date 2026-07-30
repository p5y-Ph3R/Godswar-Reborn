using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Characters;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterCheckpointIntegrationChecks
{
    private static async Task AssertPlayerOwnershipGuardAsync(
        PostgresCharacterCheckpointStore checkpointStore,
        NpgsqlDataSource dataSource,
        CheckpointFixture fixture,
        PlayerOwnershipFence owner)
    {
        var guard = new PostgresPlayerOwnershipGuard(dataSource);
        var subject =
            new CommandSubject(fixture.AccountId, fixture.CharacterId);

        var current = await guard.ValidateCurrentAsync(subject, owner);
        Check.True(
            current.IsCurrent &&
            current.StoredGeneration == owner.Generation,
            "post-commit validation accepts the exact current owner");

        var stale = await guard.ValidateCurrentAsync(
            subject,
            owner with { Generation = owner.Generation - 1 });
        Check.Equal(
            (int)PlayerOwnershipValidationStatus.OwnershipLost,
            (int)stale.Status,
            "post-commit validation rejects a stale generation");
        Check.Equal(
            owner.Generation,
            stale.StoredGeneration ?? -1,
            "stale validation reports the durable generation");

        var forgedFuture = await guard.ValidateCurrentAsync(
            subject,
            owner with { Generation = owner.Generation + 1 });
        Check.Equal(
            (int)PlayerOwnershipValidationStatus.OwnershipLost,
            (int)forgedFuture.Status,
            "a forged higher generation fails closed after cache loss");
        Check.Equal(
            owner.Generation,
            forgedFuture.StoredGeneration ?? -1,
            "future-generation rejection reports the durable generation");

        var foreignOwner = await guard.ValidateCurrentAsync(
            subject,
            owner with { OwnerId = Guid.NewGuid() });
        Check.Equal(
            (int)PlayerOwnershipValidationStatus.OwnershipLost,
            (int)foreignOwner.Status,
            "a different owner UUID cannot reuse the current generation");

        var missing = await guard.ValidateCurrentAsync(
            subject with { CharacterId = fixture.CharacterId + 1 },
            owner);
        Check.Equal(
            (int)PlayerOwnershipValidationStatus.CharacterNotFound,
            (int)missing.Status,
            "post-commit validation distinguishes a missing character");

        var invalid = await guard.ValidateCurrentAsync(subject, default);
        Check.Equal(
            (int)PlayerOwnershipValidationStatus.OwnershipLost,
            (int)invalid.Status,
            "an unbound command fence fails closed");

        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var locked = await guard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            owner);
        Check.True(
            locked.IsCurrent,
            "transaction guard accepts and locks the current owner");
        await transaction.CommitAsync();

        await using (var missingConnection =
                         await dataSource.OpenConnectionAsync())
        await using (var missingTransaction =
                         await missingConnection.BeginTransactionAsync())
        {
            var missingLocked = await guard.LockCurrentAsync(
                missingConnection,
                missingTransaction,
                subject with
                {
                    CharacterId = fixture.CharacterId + 1
                },
                owner);
            Check.Equal(
                (int)PlayerOwnershipValidationStatus.CharacterNotFound,
                (int)missingLocked.Status,
                "transaction guard distinguishes a missing character");
            await missingTransaction.RollbackAsync();
        }

        try
        {
            stale.RequireCurrent();
        }
        catch (PlayerOwnershipValidationException error)
            when (error.Status ==
                  PlayerOwnershipValidationStatus.OwnershipLost)
        {
            await AssertTransferConflictsWithValueLockAsync(
                checkpointStore,
                dataSource,
                fixture,
                owner);
            return;
        }

        throw new InvalidOperationException(
            "Assertion failed: stale ownership result raises its exact " +
            "rejection status.");
    }

    private static async Task AssertTransferConflictsWithValueLockAsync(
        PostgresCharacterCheckpointStore checkpointStore,
        NpgsqlDataSource dataSource,
        CheckpointFixture fixture,
        PlayerOwnershipFence oldOwner)
    {
        var guard = new PostgresPlayerOwnershipGuard(dataSource);
        var subject =
            new CommandSubject(fixture.AccountId, fixture.CharacterId);
        await using var valueConnection =
            await dataSource.OpenConnectionAsync();
        await using var valueTransaction =
            await valueConnection.BeginTransactionAsync();
        (await guard.LockCurrentAsync(
            valueConnection,
            valueTransaction,
            subject,
            oldOwner)).RequireCurrent();

        var replacementTask = checkpointStore.AcquireAsync(
            fixture.AccountId,
            fixture.CharacterId,
            Guid.NewGuid());
        var premature = await Task.WhenAny(
            replacementTask,
            Task.Delay(TimeSpan.FromMilliseconds(150)));
        Check.True(
            !ReferenceEquals(premature, replacementTask),
            "replacement acquisition waits for the valuable transaction lock");

        await valueTransaction.CommitAsync();
        var replacement = await replacementTask.WaitAsync(
            TimeSpan.FromSeconds(5)) ??
            throw new InvalidOperationException(
                "Replacement owner was not acquired after the value lock.");
        Check.Equal(
            oldOwner.Generation + 1,
            replacement.Owner.Generation,
            "replacement advances the durable owner generation once");

        await using var staleConnection =
            await dataSource.OpenConnectionAsync();
        await using var staleTransaction =
            await staleConnection.BeginTransactionAsync();
        var stale = await guard.LockCurrentAsync(
            staleConnection,
            staleTransaction,
            subject,
            oldOwner);
        Check.Equal(
            (int)PlayerOwnershipValidationStatus.OwnershipLost,
            (int)stale.Status,
            "old owner cannot validate after replacement commits");
        await staleTransaction.RollbackAsync();

        Check.True(
            (await guard.ValidateCurrentAsync(
                subject,
                replacement.Owner)).IsCurrent,
            "replacement owner passes post-commit current validation");
        Check.Equal(
            (int)CharacterCheckpointReleaseStatus.Released,
            (int)await checkpointStore.ReleaseAsync(
                fixture.AccountId,
                fixture.CharacterId,
                replacement.Owner),
            "replacement owner releases after the race");
        Check.Equal(
            (int)PlayerOwnershipValidationStatus.OwnershipLost,
            (int)(await guard.ValidateCurrentAsync(
                subject,
                replacement.Owner)).Status,
            "released generation cannot regain ownership");

        var reacquired = await checkpointStore.AcquireAsync(
            fixture.AccountId,
            fixture.CharacterId,
            Guid.NewGuid()) ??
            throw new InvalidOperationException(
                "Ownership was not reacquired after release.");
        Check.Equal(
            replacement.Owner.Generation + 1,
            reacquired.Owner.Generation,
            "post-release reacquisition remains monotonic");
        await checkpointStore.ReleaseAsync(
            fixture.AccountId,
            fixture.CharacterId,
            reacquired.Owner);
    }
}
