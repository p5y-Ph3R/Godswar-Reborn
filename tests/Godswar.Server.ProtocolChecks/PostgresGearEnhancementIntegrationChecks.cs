using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearEnhancementIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Gear Enhancement transactions";

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
                "SKIP PostgreSQL Gear Enhancement integration " +
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
                    "SKIP PostgreSQL Gear Enhancement integration " +
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

        await AssertOperationSuccessesAsync(connectionString);
        await AssertReplayConflictAndRejectionAsync(connectionString);
        await AssertConcurrentDuplicateAsync(connectionString);
        await AssertConcurrentDistinctAsync(connectionString);
        await AssertFaultRecoveryAsync(connectionString);
    }

    private static PostgresGearEnhancementCommandExecutor CreateExecutor(
        NpgsqlDataSource dataSource,
        IPostgresGearEnhancementCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static async Task<GearEnhancementExecutionResult>
        ExecuteAsync(
            PostgresGearEnhancementCommandExecutor executor,
            EnhancementFixture fixture,
            Guid operationId,
            int? npcId = null,
            int? dialogIndex = null,
            GearEnhancementCommandSelection? gear = null,
            GearEnhancementCommandSelection? catalyst = null,
            GearEnhancementCommandSelection? stone = null)
    {
        if (!GearEnhancementCommandEnvelope.TryCreateCommand(
                operationId,
                fixture.Operation,
                npcId ?? fixture.NpcId,
                dialogIndex ?? fixture.DialogIndex,
                gear ?? fixture.Gear,
                catalyst ?? fixture.Catalyst,
                stone ?? fixture.Stone,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid Gear Enhancement " +
                "command.");
        }

        var envelope = GearEnhancementCommandEnvelope.Create(
            fixture.Subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
        return await executor.ExecuteAsync(envelope);
    }

    private static GearEnhancementExecutionReceipt RequireReceipt(
        GearEnhancementExecutionResult result,
        GearEnhancementExecutionDisposition disposition,
        string description)
    {
        Check.Equal(
            (int)disposition,
            (int)result.Disposition,
            $"{description} disposition");
        return result.Receipt ??
            throw new InvalidOperationException(
                $"{description} returned no durable receipt.");
    }

    private static void AssertReceiptsEqual(
        GearEnhancementExecutionReceipt expected,
        GearEnhancementExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.CharacterId == actual.CharacterId &&
            expected.Operation == actual.Operation &&
            expected.NpcId == actual.NpcId &&
            expected.DialogIndex == actual.DialogIndex &&
            expected.Status == actual.Status &&
            expected.NativeResultSubId == actual.NativeResultSubId &&
            expected.Mutations.SequenceEqual(actual.Mutations) &&
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

    private sealed class ThrowingProbe(
        PostgresGearEnhancementCommandStage stage) :
        IPostgresGearEnhancementCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresGearEnhancementCommandStage reachedStage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedEnhancementFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedEnhancementFault(
        PostgresGearEnhancementCommandStage stage) : Exception
    {
        public PostgresGearEnhancementCommandStage Stage { get; } =
            stage;
    }
}
