using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Warehouse;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWarehouseCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable warehouse transfer and expansion";

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
                "SKIP PostgreSQL warehouse integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                "SKIP PostgreSQL warehouse integration requires a " +
                $"disposable B03/B09 database; received '{database}'");
            return;
        }

        await PostgresSchemaStartup.InitializeAsync(dataSource);
        var templates = await PostgresItemTemplateContentBootstrapper
            .LoadAsync(dataSource);
        var policy = await new PostgresWarehouseExpansionPolicySnapshotReader(
                dataSource,
                templates)
            .ReadAsync();
        await AssertMaximumReceiptBoundAsync(connectionString);
        await AssertFoundationGuardsAsync(connectionString);
        await AssertCheckpointLifecyclePreservesWarehouseAsync(
            connectionString,
            dataSource);
        await AssertExplicitSameItemSwapAsync(
            connectionString,
            dataSource,
            templates);
        await AssertAutomaticEmptyPrecedenceAsync(
            connectionString,
            dataSource,
            templates);
        await AssertAutomaticFanOutAndReplayAsync(
            connectionString,
            dataSource,
            templates);
        await AssertTransferRollbackAsync(
            connectionString,
            dataSource,
            templates);
        await AssertExpansionAndLostResultReplayAsync(
            connectionString,
            dataSource,
            policy);
    }

    private static PostgresWarehouseTransferCommandExecutor TransferExecutor(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog templates,
        IPostgresWarehouseCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            templates,
            probe);

    private static async Task<WarehouseTransferExecutionResult>
        ExecuteTransferAsync(
            PostgresWarehouseTransferCommandExecutor executor,
            WarehouseFixture fixture,
            Guid operationId,
            WarehouseTransferOperation operation,
            int warehouseSlot,
            int kitBagSlot,
            int destinationWarehouseSlot,
            string sourceState,
            string destinationState,
            long warehouseRevision = 0,
            long inventoryRevision = 0)
    {
        var identity = WarehouseOperationIdentity.SecureClient(operationId);
        Check.True(
            WarehouseTransferCommandEnvelope.TryCreateCommand(
                identity,
                1,
                operation,
                warehouseSlot,
                kitBagSlot,
                destinationWarehouseSlot,
                0,
                WarehouseStorageType.Normal,
                warehouseRevision,
                inventoryRevision,
                sourceState,
                destinationState,
                out var command),
            "warehouse transfer fixture creates a valid command");
        return await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                WarehouseTransferCommandEnvelope.Create(
                    fixture.Subject,
                    new CommandConnectionCorrelation(
                        Guid.NewGuid(),
                        CommandTransportKind.SecureTlsLegacy),
                    DateTimeOffset.UtcNow,
                    command)));
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "PostgreSQL returned no database name.");
    }

    private sealed class ThrowBeforeCommitProbe :
        IPostgresWarehouseCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresWarehouseCommandStage stage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == PostgresWarehouseCommandStage.TransferBeforeCommit)
            {
                throw new InjectedWarehouseFault();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedWarehouseFault : Exception;
}
