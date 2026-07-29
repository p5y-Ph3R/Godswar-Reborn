using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable equipment/bag transfer transactions";

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
                "SKIP PostgreSQL equipment/bag transfer integration " +
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
                    "SKIP PostgreSQL equipment/bag transfer " +
                    "integration requires a disposable B03/B09 " +
                    $"database; received '{databaseName}'");
                return;
            }
        }

        await using (var store =
                     new Godswar.Server.State.PostgresGameStore(
                         connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await AssertSuccessfulTransfersAsync(connectionString);
        await AssertEligibilityAndTerminalSafetyAsync(
            connectionString);
        await AssertReplayAndConflictAsync(connectionString);
        await AssertFaultRecoveryAsync(connectionString);
    }

    private static PostgresEquipmentBagTransferCommandExecutor
        CreateExecutor(
            NpgsqlDataSource dataSource,
            IPostgresEquipmentBagTransferCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static async Task<EquipmentBagTransferExecutionResult>
        ExecuteAsync(
            PostgresEquipmentBagTransferCommandExecutor executor,
            TransferFixture fixture,
            Guid operationId,
            string? expectedEquipment = null,
            string? expectedKitBag = null,
            int? equipmentSlot = null,
            int? kitBagSlot = null,
            CommandSubject? subject = null,
            bool mountRuntimeBlocked = false)
    {
        if (!EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                operationId,
                equipmentSlot ?? fixture.EquipmentSlot,
                kitBagSlot ?? fixture.KitBagSlot,
                expectedEquipment ?? fixture.EquipmentState,
                expectedKitBag ?? fixture.KitBagState,
                mountRuntimeBlocked,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid transfer command.");
        }
        return await executor.ExecuteAsync(
            EquipmentBagTransferCommandEnvelope.Create(
                subject ?? fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                DateTimeOffset.UtcNow,
                command));
    }

    private static EquipmentBagTransferExecutionReceipt
        RequireReceipt(
            EquipmentBagTransferExecutionResult result,
            EquipmentBagTransferDisposition disposition,
            EquipmentBagTransferResultStatus status,
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
        PostgresEquipmentBagTransferCommandStage stage) :
        IPostgresEquipmentBagTransferCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresEquipmentBagTransferCommandStage reachedStage,
            int ordinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedTransferFault(stage, ordinal);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedTransferFault(
        PostgresEquipmentBagTransferCommandStage stage,
        int ordinal) : Exception
    {
        public PostgresEquipmentBagTransferCommandStage Stage { get; } =
            stage;
        public int Ordinal { get; } = ordinal;
    }
}
