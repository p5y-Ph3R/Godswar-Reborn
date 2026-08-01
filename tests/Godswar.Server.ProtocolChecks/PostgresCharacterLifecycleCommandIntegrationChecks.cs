using System.Text.RegularExpressions;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable character lifecycle commands";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b11_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(databaseName))
        {
            Console.WriteLine(
                $"SKIP {CheckName} requires a disposable B03/B11 " +
                $"database; received '{databaseName}'");
            return;
        }

        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        var gameplayPublication =
            await PostgresGameplayContentPublisher.EnsurePublishedAsync(
                connectionString);

        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        var account = await CreateAccountAsync(connectionString);
        var executor = new PostgresCharacterLifecycleCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            gameplayPublication.Revision);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var token = Guid.NewGuid().ToString("N")[..8];

        var firstCreateId = Guid.NewGuid();
        var firstCreate = CreateEnvelope(
            account.Id,
            correlation,
            firstCreateId,
            $"LifeA{token}");
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(firstCreate),
            executor.ExecuteAsync(firstCreate));
        Check.Equal(
            1,
            concurrent.Count(result =>
                result.Disposition ==
                    CharacterLifecycleExecutionDisposition.Committed),
            "concurrent create UUID commits once");
        Check.Equal(
            1,
            concurrent.Count(result =>
                result.Disposition ==
                    CharacterLifecycleExecutionDisposition.Duplicate),
            "concurrent create UUID replays once");
        var first = concurrent.Single(result =>
            result.Disposition ==
                CharacterLifecycleExecutionDisposition.Committed).Receipt!;
        Check.Equal(
            1L,
            first.LifecycleVersion,
            "first account-slot transition starts at lifecycle version one");
        var starter = await ReadStarterStateAsync(
            dataSource,
            account.Id,
            first.CharacterId);
        Check.True(
            starter is
            {
                Level: 1,
                Silver: 10_000,
                Gold: 10,
                TalentPoints: 10,
                CurrentHp: 1500,
                CurrentMp: 177,
                MaxHp: 1500,
                MaxMp: 177,
                CurrentMap: GameDefaults.SpartaCapitalMap
            } &&
            starter.PositionX == GameDefaults.StartingPositionX &&
            starter.PositionZ == GameDefaults.StartingPositionZ &&
            starter.ItemCount > 0 &&
            starter.SkillCount > 0 &&
            starter.EconomyBaselineSilver == 10_000 &&
            starter.EconomyBaselineGold == 10,
            "secure creation preserves legacy starter gameplay and economy defaults");

        var createConflict = await executor.ExecuteAsync(
            CreateEnvelope(
                account.Id,
                correlation,
                firstCreateId,
                $"Other{token}"));
        Check.Equal(
            (int)CharacterLifecycleExecutionDisposition.RequestHashConflict,
            (int)createConflict.Disposition,
            "same create UUID cannot authorize a different character");

        var activeOwnerId = await SeedCheckpointLeaseAsync(
            dataSource,
            account.Id,
            first.CharacterId,
            9);
        var blockedDelete = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                new CharacterDeleteCommand(
                    Guid.NewGuid(),
                    0,
                    first.CharacterName,
                    first.CharacterId,
                    first.LifecycleVersion)));
        Check.True(
            blockedDelete is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.TerminalRejected,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.CharacterInUse,
                Receipt.LifecycleVersion: 1
            },
            "an active player ownership fence prevents deletion");
        var activeFence = await ReadCheckpointFenceAsync(
            dataSource,
            account.Id,
            first.CharacterId);
        Check.True(
            activeFence.OwnerId == activeOwnerId &&
            activeFence.OwnerGeneration == 9,
            "rejected deletion preserves the active owner generation");

        await using (var checkpointStore =
                     new PostgresCharacterCheckpointStore(dataSource))
        {
            Check.Equal(
                (int)CharacterCheckpointReleaseStatus.Released,
                (int)await checkpointStore.ReleaseAsync(
                    account.Id,
                    first.CharacterId,
                    new PlayerOwnershipFence(activeOwnerId, 9)),
                "the active session explicitly releases ownership");
        }

        var deleteId = Guid.NewGuid();
        var deleteCommand = new CharacterDeleteCommand(
            deleteId,
            0,
            first.CharacterName,
            first.CharacterId,
            first.LifecycleVersion);
        var deleted = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                deleteCommand));
        Check.True(
            deleted is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Deleted,
                Receipt.LifecycleVersion: 2
            },
            "delete atomically creates a recoverable tombstone");
        var checkpointFence = await ReadCheckpointFenceAsync(
            dataSource,
            account.Id,
            first.CharacterId);
        Check.True(
            checkpointFence.OwnerId is null &&
            checkpointFence.OwnerGeneration == 9,
            "delete leaves the released monotonic owner generation intact");

        var lostAckReplay = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                deleteCommand with
                {
                    ExpectedActiveCharacterId = null,
                    ExpectedLifecycleVersion = null
                }));
        Check.True(
            lostAckReplay.Disposition ==
                CharacterLifecycleExecutionDisposition.Duplicate &&
            lostAckReplay.Receipt == deleted.Receipt,
            "delete replays its exact receipt after active state disappears");
        var deleteConflict = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                deleteCommand with { Name = $"Wrong{token}" }));
        Check.Equal(
            (int)CharacterLifecycleExecutionDisposition.RequestHashConflict,
            (int)deleteConflict.Disposition,
            "same delete UUID with another name conflicts after deletion");

        var replacement = await executor.ExecuteAsync(
            CreateEnvelope(
                account.Id,
                correlation,
                Guid.NewGuid(),
                $"LifeB{token}"));
        Check.True(
            replacement is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Created,
                Receipt.LifecycleVersion: 3
            },
            "a tombstone does not occupy the active character slot");

        var blockedRestore = await executor.ExecuteAsync(
            RestoreEnvelope(
                account.Id,
                correlation,
                Guid.NewGuid(),
                deleted.Receipt!.CharacterId,
                deleted.Receipt.LifecycleVersion));
        Check.True(
            blockedRestore is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.TerminalRejected,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus
                        .RestoreBlockedByActiveSlot
            },
            "restore cannot replace a newer active character");

        var replacementDelete = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                new CharacterDeleteCommand(
                    Guid.NewGuid(),
                    0,
                    replacement.Receipt!.CharacterName,
                    replacement.Receipt.CharacterId,
                    replacement.Receipt.LifecycleVersion)));
        Check.Equal(
            4L,
            replacementDelete.Receipt!.LifecycleVersion,
            "replacement deletion advances the shared account-slot sequence");

        var restored = await executor.ExecuteAsync(
            RestoreEnvelope(
                account.Id,
                correlation,
                Guid.NewGuid(),
                deleted.Receipt.CharacterId,
                deleted.Receipt.LifecycleVersion));
        Check.True(
            restored is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Restored,
                Receipt.LifecycleVersion: 5
            },
            "eligible tombstone restores when the active slot is empty");

        var secondDelete = await executor.ExecuteAsync(
            DeleteEnvelope(
                account.Id,
                correlation,
                new CharacterDeleteCommand(
                    Guid.NewGuid(),
                    0,
                    restored.Receipt!.CharacterName,
                    restored.Receipt.CharacterId,
                    restored.Receipt.LifecycleVersion)));
        Check.Equal(
            6L,
            secondDelete.Receipt!.LifecycleVersion,
            "restored character can be deleted again");

        await MakePurgeEligibleAsync(
            dataSource,
            account.Id,
            secondDelete.Receipt.CharacterId);
        var purgeId = Guid.NewGuid();
        var purgeEnvelope = PurgeEnvelope(
            account.Id,
            correlation,
            purgeId,
            secondDelete.Receipt.CharacterId,
            secondDelete.Receipt.LifecycleVersion);
        var purged = await executor.ExecuteAsync(purgeEnvelope);
        Check.True(
            purged is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Purged,
                Receipt.LifecycleVersion: 7
            },
            "eligible tombstone purges and advances the slot sequence");

        var purgeReplay = await executor.ExecuteAsync(purgeEnvelope);
        Check.True(
            purgeReplay.Disposition ==
                CharacterLifecycleExecutionDisposition.Duplicate &&
            purgeReplay.Receipt == purged.Receipt,
            "purge receipt remains replayable after the character row is gone");

        var state = await ReadLifecycleStateAsync(
            dataSource,
            account.Id,
            first.CharacterId);
        Check.Equal(
            7L,
            state.AccountVersion,
            "account owns one monotonic lifecycle sequence");
        Check.Equal(
            0L,
            state.PurgedCharacterRows,
            "purge removes live character state");
        Check.Equal(
            1L,
            state.PreservedEconomyBaselines,
            "purge preserves durable economy evidence");
        Check.Equal(
            7L,
            state.OutboxEvents,
            "every committed transition emits one strict outbox event");
        Check.Equal(
            9L,
            state.InboxReceipts,
            "commits and terminal rejections retain durable receipts");
        Check.True(
            state.OutboxVersions.SequenceEqual(
                Enumerable.Range(1, 7).Select(value => (long)value)),
            "lifecycle outbox versions are contiguous across replacements");

        await AssertBackfilledFirstEventDispatchesAsync(
            connectionString,
            dataSource,
            executor,
            correlation,
            token);
        await AssertResolvedIdentitiesAreHistoricalOnlyAsync(
            connectionString,
            dataSource,
            executor,
            correlation,
            token);
    }

    private static CommandEnvelope<CharacterCreateCommand> CreateEnvelope(
        int accountId,
        CommandConnectionCorrelation correlation,
        Guid operationId,
        string name) =>
        CharacterCreateCommandEnvelope.Create(
            accountId,
            correlation,
            DateTimeOffset.UtcNow,
            new CharacterCreateCommand(
                operationId,
                0,
                name,
                1,
                GameDefaults.SpartaCamp,
                0,
                1,
                0,
                0,
                1));

    private static CommandEnvelope<CharacterDeleteCommand> DeleteEnvelope(
        int accountId,
        CommandConnectionCorrelation correlation,
        CharacterDeleteCommand command) =>
        CharacterDeleteCommandEnvelope.Create(
            accountId,
            correlation,
            DateTimeOffset.UtcNow,
            command);

    private static CommandEnvelope<CharacterRestoreCommand> RestoreEnvelope(
        int accountId,
        CommandConnectionCorrelation correlation,
        Guid operationId,
        int characterId,
        long version) =>
        CharacterRestoreCommandEnvelope.Create(
            accountId,
            correlation,
            DateTimeOffset.UtcNow,
            new CharacterRestoreCommand(
                operationId,
                0,
                characterId,
                version));

    private static CommandEnvelope<CharacterPurgeCommand> PurgeEnvelope(
        int accountId,
        CommandConnectionCorrelation correlation,
        Guid operationId,
        int characterId,
        long version) =>
        CharacterPurgeCommandEnvelope.Create(
            accountId,
            correlation,
            DateTimeOffset.UtcNow,
            new CharacterPurgeCommand(
                operationId,
                0,
                characterId,
                version));

}
