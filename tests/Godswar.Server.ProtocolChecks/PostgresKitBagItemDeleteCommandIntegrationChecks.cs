using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemDeleteCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable kit-bag item-delete transactions";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b09_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL kit-bag item-delete integration " +
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
                    "SKIP PostgreSQL kit-bag item-delete integration " +
                    "requires a disposable B03/B09 database; " +
                    $"received '{databaseName}'");
                return;
            }
        }

        await using (var store =
                     new Godswar.Server.State.PostgresGameStore(
                         connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await AssertSuccessAndReconciliationAsync(connectionString);
        await AssertReplayConflictAndOwnershipAsync(connectionString);
        await AssertTerminalRejectionsAndLateItemSafetyAsync(
            connectionString);
        await AssertFaultRecoveryAsync(connectionString);
    }

    private static PostgresKitBagItemDeleteCommandExecutor CreateExecutor(
        NpgsqlDataSource dataSource,
        IPostgresKitBagItemDeleteCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static async Task<KitBagItemDeleteExecutionResult>
        ExecuteAsync(
            PostgresKitBagItemDeleteCommandExecutor executor,
            DeleteFixture fixture,
            Guid operationId,
            string? expectedState = null,
            int? slot = null,
            CommandSubject? subject = null)
    {
        if (!KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                operationId,
                slot ?? fixture.TargetSlot,
                expectedState ?? fixture.InitialItemState,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid item-delete command.");
        }

        return await executor.ExecuteAsync(
            KitBagItemDeleteCommandEnvelope.Create(
                subject ?? fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                DateTimeOffset.UtcNow,
                command));
    }

    private static KitBagItemDeleteExecutionReceipt RequireReceipt(
        KitBagItemDeleteExecutionResult result,
        KitBagItemDeleteExecutionDisposition disposition,
        KitBagItemDeleteResultStatus status,
        string description)
    {
        Check.Equal(
            (int)disposition,
            (int)result.Disposition,
            $"{description} disposition");
        var receipt = result.Receipt ??
            throw new InvalidOperationException(
                $"{description} returned no durable receipt.");
        Check.Equal(
            (int)status,
            (int)receipt.Status,
            $"{description} status");
        return receipt;
    }

    private static void AssertReceiptsEqual(
        KitBagItemDeleteExecutionReceipt expected,
        KitBagItemDeleteExecutionReceipt actual,
        string description)
    {
        Check.True(expected == actual, description);
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

    private sealed class ThrowingProbe(
        PostgresKitBagItemDeleteCommandStage stage) :
        IPostgresKitBagItemDeleteCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresKitBagItemDeleteCommandStage reachedStage,
            int ordinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedDeleteFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedDeleteFault(
        PostgresKitBagItemDeleteCommandStage stage) : Exception
    {
        public PostgresKitBagItemDeleteCommandStage Stage { get; } =
            stage;
    }
}
