using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearMentorDecomposeIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Gear Mentor Decompose transaction";

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
                "SKIP PostgreSQL Decompose integration " +
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
                    "SKIP PostgreSQL Decompose integration requires a " +
                    "disposable B03/B09 database; " +
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

        await AssertSuccessReplayAndConflictAsync(connectionString);
        await AssertMutationBranchesAsync(connectionString);
        await AssertTerminalRejectionsAsync(connectionString);
        await AssertConcurrentDuplicateAsync(connectionString);
        await AssertConcurrentDistinctOperationsAsync(connectionString);
        await AssertFaultRecoveryAsync(connectionString);
    }

    private static PostgresGearMentorDecomposeCommandExecutor
        CreateExecutor(
            NpgsqlDataSource dataSource,
            IGearMentorDecomposeRandomSource randomSource,
            IPostgresGearMentorDecomposeCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe,
            randomSource);

    private static async Task<GearMentorDecomposeGearExecutionResult>
        ExecuteAsync(
            PostgresGearMentorDecomposeCommandExecutor executor,
            DecomposeFixture fixture,
            Guid clientOperationId,
            int? npcId = null,
            IReadOnlyList<GearMentorDecomposeSelection>? selections = null)
    {
        if (!GearMentorDecomposeGearCommandEnvelope.TryCreateCommand(
                clientOperationId,
                npcId ??
                    GearMentorDecomposeGearCommandEnvelope
                        .SpartaGearMentorNpcId,
                selections ?? fixture.Selections,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid Decompose command.");
        }

        var envelope =
            PlayerOwnershipTestFences.Bind(
                GearMentorDecomposeGearCommandEnvelope.Create(
                fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                DateTimeOffset.UtcNow,
                command));
        return await executor.ExecuteAsync(envelope);
    }

    private static GearMentorDecomposeGearExecutionReceipt
        RequireReceipt(
            GearMentorDecomposeGearExecutionResult result,
            GearMentorDecomposeGearExecutionDisposition disposition,
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
        GearMentorDecomposeGearExecutionReceipt expected,
        GearMentorDecomposeGearExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.CharacterId == actual.CharacterId &&
            expected.Status == actual.Status &&
            expected.NativeResultSubId == actual.NativeResultSubId &&
            expected.Selections.SequenceEqual(actual.Selections) &&
            expected.DustOutcomes.SequenceEqual(actual.DustOutcomes) &&
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

    private sealed class CountingRandomSource(
        Func<int, int, int>? selector = null) :
        IGearMentorDecomposeRandomSource
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public int NextIndex(int exclusiveUpperBound)
        {
            var call = Interlocked.Increment(ref _callCount) - 1;
            var selected = selector?.Invoke(
                exclusiveUpperBound,
                call) ?? 0;
            if (selected is < 0 || selected >= exclusiveUpperBound)
            {
                throw new InvalidOperationException(
                    "The test random selection is outside its bound.");
            }

            return selected;
        }
    }

    private sealed class ThrowingProbe(
        PostgresGearMentorDecomposeCommandStage stage) :
        IPostgresGearMentorDecomposeCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresGearMentorDecomposeCommandStage reachedStage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedDecomposeFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedDecomposeFault(
        PostgresGearMentorDecomposeCommandStage stage) : Exception
    {
        public PostgresGearMentorDecomposeCommandStage Stage { get; } =
            stage;
    }
}
