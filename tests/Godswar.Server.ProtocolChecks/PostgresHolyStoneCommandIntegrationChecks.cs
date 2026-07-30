using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Holy Stone transactions";

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
                "SKIP PostgreSQL Holy Stone integration " +
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
                    "SKIP PostgreSQL Holy Stone integration requires " +
                    "a disposable B03/B09 database; received " +
                    $"'{databaseName}'");
                return;
            }
        }

        await using (var store =
                     new Godswar.Server.State.PostgresGameStore(
                         connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await AssertMountSemanticsAsync(connectionString);
        await AssertTerminalSafetyAsync(connectionString);
        await AssertDrillAndRemoveAsync(connectionString);
        await AssertReplayConflictAndConcurrencyAsync(connectionString);
        await AssertStoredEvidenceBindingAsync(connectionString);
        await AssertFaultRecoveryAsync(connectionString);
    }

    private static PostgresHolyStoneCommandExecutor CreateExecutor(
        NpgsqlDataSource dataSource,
        IPostgresHolyStoneCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static async Task<HolyStoneExecutionResult> ExecuteAsync(
        PostgresHolyStoneCommandExecutor executor,
        HolyFixture fixture,
        Guid operationId,
        HolyStoneCommandOperation operation,
        int socketIndex = -1,
        string? expectedTarget = null,
        string? expectedStone = null,
        int npcId = HolyStoneCommandEnvelope.SpartaNpcId)
    {
        if (!HolyStoneCommandEnvelope.TryCreateCommand(
                operationId,
                operation,
                npcId,
                HolyStoneCommandEnvelope.DialogIndex,
                fixture.TargetLocation,
                fixture.TargetSlot,
                expectedTarget ?? fixture.TargetState,
                socketIndex,
                operation == HolyStoneCommandOperation.Mount
                    ? fixture.StoneSlot
                    : HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                operation == HolyStoneCommandOperation.Mount
                    ? expectedStone ?? fixture.StoneState
                    : "[]",
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid Holy Stone command.");
        }
        return await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                HolyStoneCommandEnvelope.Create(
                fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                DateTimeOffset.UtcNow,
                command)));
    }

    private static HolyStoneExecutionReceipt RequireReceipt(
        HolyStoneExecutionResult result,
        HolyStoneExecutionDisposition disposition,
        HolyStoneCommandResultStatus status,
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
        PostgresHolyStoneCommandStage stage) :
        IPostgresHolyStoneCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresHolyStoneCommandStage reachedStage,
            int ordinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedHolyStoneFault(stage);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedHolyStoneFault(
        PostgresHolyStoneCommandStage stage) : Exception
    {
        public PostgresHolyStoneCommandStage Stage { get; } = stage;
    }
}
