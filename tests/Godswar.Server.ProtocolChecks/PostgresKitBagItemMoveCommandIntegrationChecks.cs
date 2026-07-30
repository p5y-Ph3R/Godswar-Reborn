using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemMoveCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable kit-bag item-move transactions";

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
                "SKIP PostgreSQL kit-bag item-move integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var safety =
                     NpgsqlDataSource.Create(connectionString))
        {
            var databaseName = await ReadDatabaseNameAsync(safety);
            if (!DisposableDatabasePattern.IsMatch(databaseName))
            {
                Console.WriteLine(
                    "SKIP PostgreSQL kit-bag item-move integration " +
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

        await AssertMoveAndSwapAsync(connectionString);
        await AssertReplayAndConflictAsync(connectionString);
        await AssertTerminalSafetyAsync(connectionString);
        await AssertFaultRecoveryAsync(connectionString);
    }

    private static PostgresKitBagItemMoveCommandExecutor CreateExecutor(
        NpgsqlDataSource dataSource,
        IPostgresKitBagItemMoveCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static async Task<KitBagItemMoveExecutionResult>
        ExecuteAsync(
            PostgresKitBagItemMoveCommandExecutor executor,
            MoveFixture fixture,
            Guid operationId,
            string? expectedSource = null,
            string? expectedDestination = null,
            int? sourceSlot = null,
            int? destinationSlot = null,
            CommandSubject? subject = null)
    {
        if (!KitBagItemMoveCommandEnvelope.TryCreateCommand(
                operationId,
                sourceSlot ?? fixture.SourceSlot,
                destinationSlot ?? fixture.DestinationSlot,
                expectedSource ?? fixture.SourceState,
                expectedDestination ?? fixture.DestinationState,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid item-move command.");
        }
        return await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                KitBagItemMoveCommandEnvelope.Create(
                subject ?? fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                DateTimeOffset.UtcNow,
                command)));
    }

    private static KitBagItemMoveExecutionReceipt RequireReceipt(
        KitBagItemMoveExecutionResult result,
        KitBagItemMoveExecutionDisposition disposition,
        KitBagItemMoveResultStatus status,
        string description)
    {
        Check.Equal(
            (int)disposition,
            (int)result.Disposition,
            $"{description} disposition");
        var receipt = result.Receipt ??
            throw new InvalidOperationException(
                $"{description} returned no receipt.");
        Check.Equal(
            (int)status,
            (int)receipt.Status,
            $"{description} status");
        return receipt;
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
        PostgresKitBagItemMoveCommandStage stage,
        int? ordinal = null) :
        IPostgresKitBagItemMoveCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresKitBagItemMoveCommandStage reachedStage,
            int reachedOrdinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage &&
                (!ordinal.HasValue ||
                 ordinal.Value == reachedOrdinal))
            {
                throw new InjectedMoveFault(stage, reachedOrdinal);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedMoveFault(
        PostgresKitBagItemMoveCommandStage stage,
        int ordinal) : Exception
    {
        public PostgresKitBagItemMoveCommandStage Stage { get; } =
            stage;
        public int Ordinal { get; } = ordinal;
    }
}
