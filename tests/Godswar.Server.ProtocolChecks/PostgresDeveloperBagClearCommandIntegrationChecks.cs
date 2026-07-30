using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresDeveloperBagClearCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable developer bag-clear transaction";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

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
                "SKIP PostgreSQL durable bag-clear integration " +
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
                    "SKIP PostgreSQL durable bag-clear integration " +
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

        await AssertCommitReplayAndLateItemSafetyAsync(
            connectionString);
        await AssertConcurrentExactRetriesAsync(connectionString);
        await AssertEmptyBagTerminalReplayAsync(
            connectionString);
    }

    private static PostgresDeveloperBagClearCommandExecutor
        CreateExecutor(NpgsqlDataSource dataSource) =>
        new(dataSource, new PostgresOutboxDispatcherOptions());

    private static CommandEnvelope<DeveloperBagClearCommand>
        CreateEnvelope(
            ClearFixture fixture,
            Guid clientOperationId,
            Guid? connectionId = null)
    {
        if (!DeveloperBagClearCommandEnvelope.TryCreateCommand(
                clientOperationId,
                out var command))
        {
            throw new InvalidOperationException(
                "The bag-clear fixture requested an invalid command.");
        }

        return PlayerOwnershipTestFences.Bind(
            DeveloperBagClearCommandEnvelope.Create(
            new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId),
            new CommandConnectionCorrelation(
                connectionId ?? Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command));
    }

    private static DeveloperBagClearExecutionReceipt RequireReceipt(
        DeveloperBagClearExecutionResult result,
        DeveloperBagClearExecutionDisposition expectedDisposition,
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
        DeveloperBagClearExecutionReceipt expected,
        DeveloperBagClearExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.CharacterId == actual.CharacterId &&
            expected.RemovedSlots.SequenceEqual(actual.RemovedSlots) &&
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
