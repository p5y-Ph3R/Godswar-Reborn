using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Make Attribute Stone transaction";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const uint DustItemId = 9900;
    private const uint AttributeStoneItemId = 9930;
    private const short RecipeDustQuantity = 99;
    private const short SelectedKitBagSlot = 0;

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b(?:08|09)_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL Make Attribute Stone integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var safetySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            var databaseName =
                await ReadDatabaseNameAsync(safetySource);
            if (!DisposableDatabasePattern.IsMatch(databaseName))
            {
                Console.WriteLine(
                    "SKIP PostgreSQL Make Attribute Stone integration " +
                    "requires a disposable godswar_b03_*_smoke_XX, " +
                    $"godswar_b08_*, or godswar_b09_* database; received " +
                    $"'{databaseName}'");
                return;
            }
        }

        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await AssertCommitReplayAndConflictAsync(connectionString);
        await AssertRemainderAndInsertedOutputAsync(connectionString);
        await AssertExistingStoneStackAsync(connectionString);
        await AssertTerminalRejectionReplayAsync(connectionString);
        await AssertInvalidDustRejectionAsync(connectionString);
        await AssertInsufficientCapacityRejectionAsync(
            connectionString);
        await AssertMissingAndStaleSelectionRejectionsAsync(
            connectionString);
        await AssertConcurrentExecutorsAsync(connectionString);
        await AssertRuntimeBaselineCreationRollbackAndRetryAsync(
            connectionString);
        await AssertPreCommitFaultRollbackAsync(connectionString);
        await AssertAfterCommitRecoveryAsync(connectionString);
    }

    private static PostgresMakeAttributeStoneCommandExecutor
        CreateExecutor(
            NpgsqlDataSource dataSource,
            IPostgresMakeAttributeStoneCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static CommandEnvelope<GearMentorMakeAttributeStoneCommand>
        CreateEnvelope(
            StoneFixture fixture,
            Guid clientOperationId,
            int? npcId = null,
            Guid? connectionId = null)
    {
        if (!GearMentorMakeAttributeStoneCommandEnvelope.TryCreateCommand(
                clientOperationId,
                npcId ??
                    GearMentorMakeAttributeStoneCommandEnvelope
                        .SpartaGearMentorNpcId,
                SelectedKitBagSlot,
                fixture.ExpectedSelectedState,
                out var command))
        {
            throw new InvalidOperationException(
                "The integration fixture requested an invalid " +
                "Make Attribute Stone command.");
        }

        return GearMentorMakeAttributeStoneCommandEnvelope.Create(
            new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId),
            new CommandConnectionCorrelation(
                connectionId ?? Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
    }

    private static MakeAttributeStoneExecutionReceipt RequireReceipt(
        MakeAttributeStoneExecutionResult result,
        MakeAttributeStoneExecutionDisposition expectedDisposition,
        string description)
    {
        Check.True(
            result.Disposition == expectedDisposition,
            $"{description} disposition");
        return result.Receipt ??
               throw new InvalidOperationException(
                   $"{description} returned no durable receipt.");
    }

    private static void AssertReceiptsEqual(
        MakeAttributeStoneExecutionReceipt expected,
        MakeAttributeStoneExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.CharacterId == actual.CharacterId &&
            expected.Status == actual.Status &&
            expected.NativeResultSubId == actual.NativeResultSubId &&
            expected.SelectedKitBagSlot ==
                actual.SelectedKitBagSlot &&
            expected.SourceDustItemId == actual.SourceDustItemId &&
            expected.OutputStoneItemId == actual.OutputStoneItemId &&
            expected.IsBound == actual.IsBound &&
            expected.InventoryRevision == actual.InventoryRevision &&
            string.Equals(
                expected.AuditReference,
                actual.AuditReference,
                StringComparison.Ordinal) &&
            expected.OutboxEventId == actual.OutboxEventId,
            description);
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
               throw new InvalidDataException(
                   "PostgreSQL returned no current database name.");
    }
}
