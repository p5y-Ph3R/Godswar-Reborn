using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Reconciliation;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresB19ReconciliationIntegrationChecks
{
    internal const string BoundedCheckName =
        "PostgreSQL bounded economy reconciliation";
    internal const string RestoredCheckName =
        "PostgreSQL restored reconciliation verification";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunBoundedAsync()
    {
        var connectionString = ReadConnectionString();
        if (connectionString is null)
        {
            Console.WriteLine(
                $"SKIP {BoundedCheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await RequireDisposableDatabaseAsync(dataSource, "source");
        var migrationRunner =
            new PostgresSchemaMigrationRunner(dataSource);
        await migrationRunner.InitializeGodswarSchemaAsync();
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);

        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            _ = await EnsureEconomyFixtureAsync(store);
            await EnsureCliTruncationSentinelAsync(store);
        }
        _ = await PostgresNpcContentBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresMonsterContentBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresEnterBootstrapBaselinePublisher
            .EnsurePublishedAsync(connectionString);

        await AssertCurrentMigrationManifestAsync(dataSource);
        var fixture = await ReadEconomyFixtureAsync(dataSource);
        var runner = await CreateRunnerAsync(dataSource);
        var clean = await runner.RunAsync();
        AssertCompleted(clean, "initial source reconciliation");
        AssertNoFindings(
            clean,
            "the synthetic source starts with zero unexplained mismatch");

        await ApplyEconomyDriftAsync(dataSource, fixture);
        var driftedBefore =
            await ReadFixtureFingerprintAsync(dataSource, fixture);
        var firstDriftReport = await runner.RunAsync();
        var driftedAfter =
            await ReadFixtureFingerprintAsync(dataSource, fixture);
        Check.Equal(
            driftedBefore,
            driftedAfter,
            "report-only reconciliation never mutates drifted value");
        Check.True(
            Find(firstDriftReport,
                ReconciliationCategory.WalletBalanceMismatch) >= 1,
            "a deliberately drifted wallet is detected");
        Check.True(
            Find(firstDriftReport,
                ReconciliationCategory
                    .InventoryBaselineSnapshotMismatch) >= 1 &&
            Find(firstDriftReport,
                ReconciliationCategory.InventoryItemsMismatch) >= 1,
            "a deliberately drifted inventory row is detected");

        var secondDriftReport = await runner.RunAsync();
        Check.True(
            CanonicalFindings(firstDriftReport).SequenceEqual(
                CanonicalFindings(secondDriftReport)),
            "repeated report-only scans classify the same drift");
        Check.Equal(
            driftedBefore,
            await ReadFixtureFingerprintAsync(dataSource, fixture),
            "duplicate reports remain value-immutable");
        await AssertCliEvidenceAsync(connectionString, fixture);

        await RestoreEconomyFixtureAsync(dataSource, fixture);
        var restoredEconomy = await runner.RunAsync();
        AssertCompleted(
            restoredEconomy,
            "source reconciliation after fixture cleanup");
        AssertNoFindings(
            restoredEconomy,
            "test cleanup restores a zero-mismatch source");

        await SeedExpectedPurgedCharacterAsync(
            connectionString,
            dataSource);
        await AssertLedgerChainTamperingAsync(
            dataSource,
            fixture,
            runner);
        var expiredEventId =
            await SeedExpiredOutboxLeaseAsync(dataSource);
        var dispatcher = new PostgresOutboxDispatcher(
            dataSource,
            PostgresOutboxConsumerCatalog.Create(),
            new PostgresOutboxDispatcherOptions(),
            leaseOwner: "b19-reconciliation-check");
        var repairer =
            new PostgresExpiredOutboxLeaseRepairer(dispatcher);
        await AssertRepairBoundsAsync(repairer);
        var concurrent = await Task.WhenAll(
            repairer.RecoverExpiredOutboxLeasesAsync(1),
            repairer.RecoverExpiredOutboxLeasesAsync(1));
        Check.Equal(
            1,
            concurrent.Sum(static result => result.RecoveredCount),
            "concurrent safe repairers recover one expired lease once");
        Check.Equal(
            1,
            concurrent.Count(static result =>
                result.RecoveredCount == 1),
            "only one concurrent repairer wins the row lock");
        var duplicate =
            await repairer.RecoverExpiredOutboxLeasesAsync(1);
        Check.Equal(
            0,
            duplicate.RecoveredCount,
            "replaying expired-lease repair is idempotent");
        await AssertRecoveredLeaseAsync(dataSource, expiredEventId);
        await AssertOutboxPositionMismatchAsync(
            dataSource,
            runner,
            expiredEventId);
        await SeedBenignPendingOutboxAsync(dataSource, fixture);

        var final = await runner.RunAsync();
        AssertCompleted(final, "final source reconciliation");
        Check.Equal(
            0L,
            Find(final, ReconciliationCategory.OutboxExpiredLease),
            "safe repair removes the expired-lease finding");
        AssertNoFindings(
            final,
            "safe outbox repair leaves zero unexplained mismatch");
        await AssertAllEconomyViewsCleanAsync(dataSource);

        Console.WriteLine(
            "B19 source fixture, report-only drift detection, bounded " +
            "lease recovery, and clean final state verified.");
    }

    public static async Task RunRestoredAsync()
    {
        var connectionString = ReadConnectionString();
        if (connectionString is null)
        {
            Console.WriteLine(
                $"SKIP {RestoredCheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await RequireDisposableDatabaseAsync(dataSource, "restored");
        await AssertCurrentMigrationManifestAsync(dataSource);
        var fixture = await ReadEconomyFixtureAsync(dataSource);
        await AssertRecoveredLeaseMarkerAsync(dataSource);
        await AssertAllEconomyViewsCleanAsync(dataSource);

        var before = await ReadDurableFingerprintAsync(dataSource);
        var report = await (
            await CreateRunnerAsync(dataSource)).RunAsync();
        var after = await ReadDurableFingerprintAsync(dataSource);

        AssertCompleted(report, "restored database reconciliation");
        AssertNoFindings(
            report,
            "restored state has zero unexplained mismatch");
        Check.Equal(
            before,
            after,
            "restored verification is read-only and idempotent");
        Check.True(
            fixture.CharacterId > 0 && fixture.ItemId > 0,
            "the deterministic B19 fixture survives dump and restore");

        Console.WriteLine(
            "B19 restored migration manifest, durable fixture, clean " +
            "reconciliation, and read-only fingerprint verified.");
    }

    private static async Task<ReconciliationRunner> CreateRunnerAsync(
        NpgsqlDataSource dataSource)
    {
        var itemTemplates =
            await PostgresItemTemplateCatalogLoader.LoadAsync(dataSource);
        return new ReconciliationRunner(
            new PostgresReconciliationReader(
                dataSource,
                itemTemplates.Revision.Sha256),
            new ReconciliationOptions
            {
                BatchSize = 2,
                MaximumCharactersPerRun = 100_000,
                MaximumOutboxEventsPerRun = 100_000,
                PollIntervalMilliseconds = 10_000,
                CommandTimeoutMilliseconds = 5_000,
                RunTimeoutMilliseconds = 30_000
            });
    }

    private static void AssertCompleted(
        ReconciliationReport report,
        string description)
    {
        Check.Equal(
            (int)ReconciliationRunStatus.Completed,
            (int)report.Status,
            description);
        Check.True(!report.Truncated, $"{description} is not truncated");
    }

    private static void AssertNoFindings(
        ReconciliationReport report,
        string description)
    {
        Check.True(
            report.Findings.Count == 0,
            $"{description}; findings=" +
            string.Join(
                ",",
                report.Findings.Select(static finding =>
                    $"{finding.Category.ToProtocolValue()}:" +
                    finding.Count)));
    }

    private static long Find(
        ReconciliationReport report,
        ReconciliationCategory category) =>
        report.Findings
            .Where(finding => finding.Category == category)
            .Sum(static finding => finding.Count);

    private static IEnumerable<string> CanonicalFindings(
        ReconciliationReport report) =>
        report.Findings.Select(static finding =>
            $"{(byte)finding.Category}:{finding.Count}");
}
