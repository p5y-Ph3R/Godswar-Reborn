using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridUpgradeCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Zodiac skill-grid upgrade";

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
                "SKIP PostgreSQL Zodiac upgrade integration " +
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
                    "SKIP PostgreSQL Zodiac upgrade integration " +
                    "requires a disposable B03/B09 database; received " +
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

        await AssertSuccessAndOwnershipAsync(connectionString);
        await AssertReplayRejectionAndConflictAsync(connectionString);
        await AssertConcurrencyAsync(connectionString);
        await AssertFaultAtomicityAsync(connectionString);
    }

    private static PostgresZodiacSkillGridUpgradeCommandExecutor
        CreateExecutor(
            NpgsqlDataSource dataSource,
            IPostgresZodiacSkillGridUpgradeCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static CommandEnvelope<ZodiacSkillGridUpgradeCommand>
        CreateEnvelope(
            ZodiacUpgradeFixture fixture,
            int gridIndex,
            Guid operationId,
            CommandSubject? subject = null,
            Guid? connectionId = null)
    {
        if (!ZodiacSkillGridUpgradeCommandEnvelope.TryCreateCommand(
                operationId,
                gridIndex,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid Zodiac upgrade.");
        }

        return ZodiacSkillGridUpgradeCommandEnvelope.Create(
            subject ?? fixture.Subject,
            new CommandConnectionCorrelation(
                connectionId ?? Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
    }

    private static ZodiacSkillGridUpgradeExecutionReceipt
        RequireReceipt(
            ZodiacSkillGridUpgradeExecutionResult result,
            ZodiacSkillGridUpgradeExecutionDisposition disposition,
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
        PostgresZodiacSkillGridUpgradeCommandStage stage) :
        IPostgresZodiacSkillGridUpgradeCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresZodiacSkillGridUpgradeCommandStage reachedStage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedZodiacUpgradeFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedZodiacUpgradeFault(
        PostgresZodiacSkillGridUpgradeCommandStage stage) : Exception
    {
        public PostgresZodiacSkillGridUpgradeCommandStage Stage { get; } =
            stage;
    }
}
