using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresEquipmentForgeCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable equipment-forge transactions";

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
                "SKIP PostgreSQL equipment-forge integration " +
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
                    "SKIP PostgreSQL equipment-forge integration " +
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

        await AssertSuccessAndFailedRollAsync(connectionString);
        await AssertZeroSilverAndTerminalReplayAsync(connectionString);
        await AssertReplayConflictAndRacesAsync(connectionString);
        await AssertFaultRecoveryAsync(connectionString);
    }

    private static PostgresEquipmentForgeCommandExecutor CreateExecutor(
        NpgsqlDataSource dataSource,
        Func<int> rollSource,
        IPostgresEquipmentForgeCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe,
            rollSource);

    private static async Task<EquipmentForgeExecutionResult>
        ExecuteAsync(
            PostgresEquipmentForgeCommandExecutor executor,
            ForgeFixture fixture,
            Guid operationId,
            EquipmentForgeCommandSelection? equipment = null,
            IReadOnlyList<EquipmentForgeCommandSelection>? odds = null)
    {
        if (!EquipmentForgeCommandEnvelope.TryCreateCommand(
                operationId,
                equipment ?? fixture.Equipment,
                fixture.Primary,
                odds ?? fixture.Odds,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid forge command.");
        }

        return await executor.ExecuteAsync(
            EquipmentForgeCommandEnvelope.Create(
                fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                DateTimeOffset.UtcNow,
                command));
    }

    private static EquipmentForgeExecutionReceipt RequireReceipt(
        EquipmentForgeExecutionResult result,
        EquipmentForgeExecutionDisposition disposition,
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
        EquipmentForgeExecutionReceipt expected,
        EquipmentForgeExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.CharacterId == actual.CharacterId &&
            expected.Status == actual.Status &&
            expected.MaterialType == actual.MaterialType &&
            expected.Roll == actual.Roll &&
            expected.Probability == actual.Probability &&
            expected.SilverSpent == actual.SilverSpent &&
            string.Equals(
                expected.EquipmentBeforeCompactItemState,
                actual.EquipmentBeforeCompactItemState,
                StringComparison.Ordinal) &&
            string.Equals(
                expected.EquipmentAfterCompactItemState,
                actual.EquipmentAfterCompactItemState,
                StringComparison.Ordinal) &&
            expected.Materials.SequenceEqual(actual.Materials) &&
            expected.WalletRevision == actual.WalletRevision &&
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

    private sealed class CountingRollSource(int value)
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public int Next()
        {
            Interlocked.Increment(ref _calls);
            return value;
        }
    }

    private sealed class ThrowingProbe(
        PostgresEquipmentForgeCommandStage stage,
        int ordinal = -1) :
        IPostgresEquipmentForgeCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresEquipmentForgeCommandStage reachedStage,
            int reachedOrdinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage &&
                (ordinal < 0 || ordinal == reachedOrdinal))
            {
                throw new InjectedForgeFault(stage, reachedOrdinal);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedForgeFault(
        PostgresEquipmentForgeCommandStage stage,
        int ordinal) : Exception
    {
        public PostgresEquipmentForgeCommandStage Stage { get; } = stage;
        public int Ordinal { get; } = ordinal;
    }
}
