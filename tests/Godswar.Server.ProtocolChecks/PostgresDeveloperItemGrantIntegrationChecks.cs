using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const uint MaterialItemId = 4230;
    private const int GrantQuantity = 7;

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
                "SKIP PostgreSQL developer-item grant integration " +
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
                    "SKIP PostgreSQL developer-item grant integration " +
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
        await AssertMountGrantCommitReplayAndConflictAsync(
            connectionString);
        await AssertConcurrentExecutorsAsync(connectionString);
        await AssertInsufficientCapacityAsync(connectionString);
        await AssertLazyRuntimeCutoverAsync(connectionString);
        await AssertConcurrentLegacyDeleteIsFencedAsync(
            connectionString);
        await AssertUnbaselinedAdvancedRevisionFailsClosedAsync(
            connectionString);
        await AssertPreCommitFaultRollbackAsync(connectionString);
        await AssertAfterCommitRecoveryAsync(connectionString);
    }

    private static PostgresDeveloperItemGrantCommandExecutor
        CreateExecutor(
            NpgsqlDataSource dataSource,
            IPostgresDeveloperItemGrantCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static CommandEnvelope<DeveloperItemGrantCommand>
        CreateEnvelope(
            GrantFixture fixture,
            Guid clientOperationId,
            int quantity = GrantQuantity,
            Guid? connectionId = null)
    {
        if (!DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                MaterialItemId,
                quantity,
                clientOperationId,
                out var command))
        {
            throw new InvalidOperationException(
                "The integration fixture requested an invalid grant.");
        }

        return DeveloperItemGrantCommandEnvelope.Create(
            new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId),
            new CommandConnectionCorrelation(
                connectionId ?? Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command);
    }

    private static DeveloperItemGrantExecutionReceipt RequireReceipt(
        DeveloperItemGrantExecutionResult result,
        DeveloperItemGrantExecutionDisposition expectedDisposition,
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
        DeveloperItemGrantExecutionReceipt expected,
        DeveloperItemGrantExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.CharacterId == actual.CharacterId &&
            expected.ItemId == actual.ItemId &&
            expected.GrantedQuantity == actual.GrantedQuantity &&
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
