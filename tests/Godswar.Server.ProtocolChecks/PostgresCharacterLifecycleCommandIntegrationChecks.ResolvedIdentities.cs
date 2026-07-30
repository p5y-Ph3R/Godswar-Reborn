using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Characters;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleCommandIntegrationChecks
{
    private static async Task
        AssertResolvedIdentitiesAreHistoricalOnlyAsync(
            string connectionString,
            NpgsqlDataSource dataSource,
            PostgresCharacterLifecycleCommandExecutor executor,
            CommandConnectionCorrelation correlation,
            string token)
    {
        var account = await CreateAccountAsync(connectionString);
        var reusableName = $"Reuse{token}";
        var original = await executor.ExecuteAsync(
            CreateEnvelope(
                account.Id,
                correlation,
                Guid.NewGuid(),
                reusableName));
        var originalDeleteCommand = new CharacterDeleteCommand(
            Guid.NewGuid(),
            CharacterLifecycleCommandContract.SingleCharacterSlot,
            reusableName,
            original.Receipt!.CharacterId,
            original.Receipt.LifecycleVersion);
        var originalDelete = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                originalDeleteCommand));

        await MakePurgeEligibleAsync(
            dataSource,
            account.Id,
            originalDelete.Receipt!.CharacterId);
        var originalPurgeEnvelope = PurgeEnvelope(
            account.Id,
            correlation,
            Guid.NewGuid(),
            originalDelete.Receipt.CharacterId,
            originalDelete.Receipt.LifecycleVersion);
        var originalPurge = await executor.ExecuteAsync(
            originalPurgeEnvelope);
        Check.True(
            originalPurge is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Purged,
                Receipt.LifecycleVersion: 3
            },
            "historical-identity fixture purges the original character");

        var replacement = await executor.ExecuteAsync(
            CreateEnvelope(
                account.Id,
                correlation,
                Guid.NewGuid(),
                reusableName));
        Check.True(
            replacement is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Created,
                Receipt.LifecycleVersion: 4
            },
            "purge permits a replacement to reuse the historical name");

        var oldDeleteReplay = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                originalDeleteCommand with
                {
                    ExpectedActiveCharacterId =
                        replacement.Receipt!.CharacterId,
                    ExpectedLifecycleVersion =
                        replacement.Receipt.LifecycleVersion
                }));
        Check.True(
            oldDeleteReplay.Disposition ==
                CharacterLifecycleExecutionDisposition.Duplicate &&
            oldDeleteReplay.Receipt == originalDelete.Receipt,
            "resolved delete UUID returns only its historical receipt");
        var activeAfterOldDelete = await ReadLifecycleRowAsync(
            dataSource,
            account.Id,
            replacement.Receipt.CharacterId);
        Check.True(
            activeAfterOldDelete is
            {
                LifecycleState: "active",
                LifecycleVersion: 4
            },
            "resolved delete UUID cannot act on a same-name replacement");

        var replacementDelete = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                new CharacterDeleteCommand(
                    Guid.NewGuid(),
                    CharacterLifecycleCommandContract
                        .SingleCharacterSlot,
                    reusableName,
                    replacement.Receipt.CharacterId,
                    replacement.Receipt.LifecycleVersion)));
        Check.Equal(
            5L,
            replacementDelete.Receipt!.LifecycleVersion,
            "a fresh delete UUID performs the replacement transition");

        var retargetedOldPurge = await executor.ExecuteAsync(
            PurgeEnvelope(
                account.Id,
                correlation,
                originalPurgeEnvelope.Command.ClientOperationId,
                replacementDelete.Receipt.CharacterId,
                replacementDelete.Receipt.LifecycleVersion));
        Check.Equal(
            (int)CharacterLifecycleExecutionDisposition
                .RequestHashConflict,
            (int)retargetedOldPurge.Disposition,
            "resolved purge UUID cannot be retargeted as fresh intent");

        var oldPurgeReplay = await executor.ExecuteAsync(
            originalPurgeEnvelope);
        Check.True(
            oldPurgeReplay.Disposition ==
                CharacterLifecycleExecutionDisposition.Duplicate &&
            oldPurgeReplay.Receipt == originalPurge.Receipt,
            "resolved purge UUID returns only its historical receipt");
        var replacementTombstone = await ReadLifecycleRowAsync(
            dataSource,
            account.Id,
            replacement.Receipt.CharacterId);
        Check.True(
            replacementTombstone is
            {
                LifecycleState: "deleted",
                LifecycleVersion: 5
            },
            "resolved purge UUID cannot remove a replacement tombstone");
    }

    private static async Task<LifecycleRow?> ReadLifecycleRowAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT lifecycle_state, lifecycle_version
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new LifecycleRow(
                reader.GetString(0),
                reader.GetInt64(1))
            : null;
    }

    private sealed record LifecycleRow(
        string LifecycleState,
        long LifecycleVersion);
}
