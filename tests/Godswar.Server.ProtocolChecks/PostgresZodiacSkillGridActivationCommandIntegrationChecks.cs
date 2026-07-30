using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridActivationCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Zodiac skill-grid activation";

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
                "SKIP PostgreSQL Zodiac activation integration " +
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
                    "SKIP PostgreSQL Zodiac activation integration " +
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

        await AssertOwnershipAndSuccessAsync(connectionString);
        await AssertReplayAndRecoveryAsync(connectionString);
        await AssertFaultAtomicityAsync(connectionString);
    }

    private static PostgresZodiacSkillGridActivationCommandExecutor
        CreateExecutor(
            NpgsqlDataSource dataSource,
            IPostgresZodiacSkillGridActivationCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static CommandEnvelope<ZodiacSkillGridActivationCommand>
        CreateEnvelope(
            ZodiacActivationFixture fixture,
            int gridIndex,
            int expectedLevel =
                ZodiacSkillGridActivationCommandEnvelope
                    .ExpectedInactiveLevel,
            CommandSubject? subject = null,
            Guid? connectionId = null)
    {
        if (!ZodiacSkillGridActivationCommandEnvelope.TryCreateCommand(
                gridIndex,
                expectedLevel,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid Zodiac activation.");
        }

        return ZodiacSkillGridActivationCommandEnvelope.Create(
            subject ?? fixture.Subject,
            new CommandConnectionCorrelation(
                connectionId ?? Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
    }

    private static ZodiacSkillGridActivationExecutionReceipt
        RequireReceipt(
            ZodiacSkillGridActivationExecutionResult result,
            ZodiacSkillGridActivationExecutionDisposition disposition,
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
        PostgresZodiacSkillGridActivationCommandStage stage) :
        IPostgresZodiacSkillGridActivationCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresZodiacSkillGridActivationCommandStage reachedStage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedZodiacActivationFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedZodiacActivationFault(
        PostgresZodiacSkillGridActivationCommandStage stage) : Exception
    {
        public PostgresZodiacSkillGridActivationCommandStage Stage { get; } =
            stage;
    }
}
