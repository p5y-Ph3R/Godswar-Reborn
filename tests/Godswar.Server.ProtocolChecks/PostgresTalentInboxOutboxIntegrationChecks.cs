using System.Globalization;
using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Talents;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresTalentInboxOutboxIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const int TalentId = 0;

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b08_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL talent inbox/outbox integration " +
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
                    "SKIP PostgreSQL talent inbox/outbox integration " +
                    $"requires a disposable godswar_b03_*_smoke_XX or " +
                    $"godswar_b08_* database; received '{databaseName}'");
                return;
            }
        }

        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await AssertFirstCommitAndReplayAsync(connectionString);
        await AssertProfessionChangedReplayAsync(connectionString);
        await AssertConcurrentExecutorsAsync(connectionString);
        await AssertRejectedCommandsAndCorrectionAsync(
            connectionString);
        await AssertDurableHashConflictAsync(connectionString);
        await AssertPreCommitFaultRollbackAsync(connectionString);
        await AssertAfterCommitRecoveryAsync(connectionString);
    }

    private static PostgresTalentUpgradeCommandExecutor CreateExecutor(
        NpgsqlDataSource dataSource,
        IPostgresTalentUpgradeCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            probe);

    private static CommandEnvelope<TalentUpgradeCommand> CreateEnvelope(
        TalentFixture fixture,
        int expectedRank,
        int? accountId = null,
        Guid? connectionId = null)
    {
        if (!TalentUpgradeCommandEnvelope.TryCreateCommand(
                TalentId,
                expectedRank,
                out var command))
        {
            throw new InvalidOperationException(
                "The integration fixture requested an invalid talent intent.");
        }

        return TalentUpgradeCommandEnvelope.Create(
            new CommandSubject(
                accountId ?? fixture.AccountId,
                fixture.CharacterId),
            new CommandConnectionCorrelation(
                connectionId ?? Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command);
    }

    private static TalentUpgradeExecutionReceipt RequireReceipt(
        TalentUpgradeExecutionResult result,
        TalentUpgradeExecutionDisposition expectedDisposition,
        string description)
    {
        Check.True(
            result.Disposition == expectedDisposition,
            $"{description} disposition");
        return result.Receipt ??
               throw new InvalidOperationException(
                   $"{description} returned no durable receipt.");
    }

    private static void AssertReceiptsEqual(
        TalentUpgradeExecutionReceipt expected,
        TalentUpgradeExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.CharacterId == actual.CharacterId &&
            expected.TalentId == actual.TalentId &&
            expected.Rank == actual.Rank &&
            expected.Cost == actual.Cost &&
            expected.RemainingTalentPoints ==
                actual.RemainingTalentPoints &&
            expected.DisplayValue == actual.DisplayValue &&
            expected.AggregateRevision ==
                actual.AggregateRevision &&
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

    private static string PrincipalKey(TalentFixture fixture) =>
        fixture.AccountId.ToString(CultureInfo.InvariantCulture);

    private static string AggregateKey(TalentFixture fixture) =>
        TalentUpgradePersistenceCodec.AggregateKey(
            fixture.CharacterId,
            TalentId);
}
