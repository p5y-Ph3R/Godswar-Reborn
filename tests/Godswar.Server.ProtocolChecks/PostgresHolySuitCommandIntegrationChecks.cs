using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySuitCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL authoritative Holy Suit transactions";

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
                "SKIP PostgreSQL Holy Suit integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var safety = NpgsqlDataSource.Create(connectionString))
        {
            var command = safety.CreateCommand("SELECT current_database();");
            var database = await command.ExecuteScalarAsync() as string ?? "";
            if (!DisposableDatabasePattern.IsMatch(database))
            {
                Console.WriteLine(
                    "SKIP PostgreSQL Holy Suit integration requires a " +
                    $"disposable B03/B09 database; received '{database}'");
                return;
            }
        }

        await using (var migrationSource =
            NpgsqlDataSource.Create(connectionString))
        {
            await new PostgresSchemaMigrationRunner(migrationSource)
                .InitializeGodswarSchemaAsync();
        }
        GameplayItemContent itemContent;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            itemContent = store.ItemContent;
        }
        await AssertWorkflowAndReplayAsync(connectionString, itemContent);
        await AssertAutomaticMaximumAsync(connectionString, itemContent);
        await AssertBoxFiveCapacityAsync(connectionString, itemContent);
        await AssertRealmQuotaIsolationAsync(connectionString, itemContent);
        await AssertAdversarialAuthorityAsync(connectionString, itemContent);
    }

    private static async Task AssertWorkflowAndReplayAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        var fixture = await CreateFixtureAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = new PostgresHolySuitCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            itemContent);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var ownership = PlayerOwnershipTestFences.ForCharacter(
            fixture.CharacterId);
        var initialQuota = await executor.ReadStoreQuotaAsync(
            fixture.Subject,
            ownership);
        Check.True(
            initialQuota.CharacterLevel == 80 &&
            initialQuota.StoredExperienceToday == 0 &&
            initialQuota.DailyExperienceCredit == 2_000_000_000 &&
            !initialQuota.BattlePassDailyLimitExempt,
            "initial Store EXP page quota is authoritative");

        var encodedMaximumRejected = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 0,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: uint.MaxValue);
        var encodedMaximumReceipt = Require(
            encodedMaximumRejected,
            HolySuitExecutionDisposition.TerminalRejected,
            HolySuitCommandResultStatus.HolyBoxFull,
            "UInt32 maximum reaches selected-box capacity rejection");
        Check.True(
            encodedMaximumReceipt.RequestedExperience == uint.MaxValue &&
            encodedMaximumReceipt.NativeResultSubId ==
                HolySuitNativeResults.HolyBoxFullSubId &&
            encodedMaximumReceipt.Mutations.Length == 0,
            "oversized UInt32 request returns durable capacity result 1800 " +
            "without mutation");

        var firstStoreId = Guid.NewGuid();
        var store = await ExecuteAsync(
            executor,
            fixture,
            connection,
            firstStoreId,
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 0,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 50_000_000);
        var storeReceipt = Require(
            store,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.ExperienceStored,
            "store 50m EXP");
        var usedQuota = await executor.ReadStoreQuotaAsync(
            fixture.Subject,
            ownership);
        Check.Equal(
            50_000_000L,
            usedQuota.StoredExperienceToday,
            "Store EXP quota read observes committed realm-day usage");

        var duplicate = await ExecuteAsync(
            executor,
            fixture,
            connection,
            firstStoreId,
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 0,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 50_000_000);
        Require(
            duplicate,
            HolySuitExecutionDisposition.Duplicate,
            HolySuitCommandResultStatus.ExperienceStored,
            "replay does not spend EXP twice");

        await SetDailyStoredExperienceAsync(
            connectionString,
            fixture,
            initialQuota.UsageDay,
            1_980_000_000);

        var dailyRejected = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 5,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 40_000_000);
        Require(
            dailyRejected,
            HolySuitExecutionDisposition.TerminalRejected,
            HolySuitCommandResultStatus.DailyStoreLimitExceeded,
            "fixed 2b daily limit");

        await AddBattlePassAsync(connectionString, fixture.AccountId);
        var bypassed = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 5,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 40_000_000);
        var bypassReceipt = Require(
            bypassed,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.ExperienceStored,
            "active battle pass bypasses only daily quota");
        Check.True(
            bypassReceipt.BattlePassDailyLimitExempt,
            "battle-pass exemption is durable evidence");
        var unlimitedQuota = await executor.ReadStoreQuotaAsync(
            fixture.Subject,
            ownership);
        Check.True(
            unlimitedQuota.StoredExperienceToday == 2_020_000_000 &&
            unlimitedQuota.DailyExperienceCredit == 2_000_000_000 &&
            unlimitedQuota.BattlePassDailyLimitExempt,
            "battle pass preserves usage and marks daily enforcement exempt");

        var transfer = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.TransferExperience,
            primarySlot: 1,
            primaryState: HolySpiritGear().ToCompactString(),
            secondarySlot: 0,
            secondaryState: storeReceipt.Mutations[0]
                .AfterCompactItemState);
        var transferReceipt = Require(
            transfer,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.ExperienceTransferred,
            "full box transfers and is deleted");
        Check.Equal(
            797,
            CompactItemEntry.Parse(
                transferReceipt.Mutations[0].AfterCompactItemState)
                .Socket1Value!.Value,
            "Holy Suit transfer preserves rolled Holy Spirit value");

        var ware = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.ConsumeWare,
            primarySlot: 1,
            primaryState: transferReceipt.Mutations[0]
                .AfterCompactItemState,
            secondarySlot: 3,
            secondaryState: Item(9010, stack: 99).ToCompactString());
        var wareReceipt = Require(
            ware,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.WareConsumed,
            "Bronze ware consumes one and upgrades deterministically");
        Check.Equal(
            101,
            CompactItemEntry.Parse(
                wareReceipt.Mutations[0].AfterCompactItemState)
                .HolySuitCode,
            "Common gear becomes Bronze level 1");

        var mithril = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.ConsumeWare,
            primarySlot: 2,
            primaryState: Item(1007, suit: 501).ToCompactString(),
            secondarySlot: 4,
            secondaryState: Item(9014, stack: 99).ToCompactString());
        var mithrilReceipt = Require(
            mithril,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.WareConsumed,
            "Mithril ware automatically consumes experience prisms");
        Check.True(
            mithrilReceipt.PrismsConsumed == 12 &&
            CompactItemEntry.Parse(
                mithrilReceipt.Mutations[0].AfterCompactItemState)
                .HolySuitCode == 502,
            "Mithril level 1 to 2 consumes 12 prisms and reaches 502");

        var transform = await ExecuteAsync(
            executor,
            fixture,
            connection,
            Guid.NewGuid(),
            HolySuitCommandOperation.TransformExperience,
            prisms: 2);
        Require(
            transform,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.ExperienceTransformed,
            "200m EXP creates two bound prisms");

        var state = await ReadStateAsync(connectionString, fixture);
        Check.Equal(
            3_710_000_000L,
            state.Experience,
            "exact UInt32-range player EXP debit");
        Check.Equal(3L, state.ProgressionRevision, "EXP revision advances three times");
        Check.Equal(6L, state.InventoryRevision, "six inventory commits");
        Check.Equal(
            2_020_000_000L,
            state.DailyStored,
            "usage records pass-exempt storage above the fixed cap");
        Check.Equal(6L, state.OutboxCount, "only committed commands publish");
        Check.Equal(8L, state.InboxCount, "terminal rejections are replayable");
        Check.Equal(8L, state.AuditCount, "every first execution is audited");
        Check.Equal(10L, state.InventoryLedgerCount, "every item change is ledgered");
        Check.Equal(1L, state.DuplicateCount, "duplicate request is counted once");
        Check.Equal(0L, state.ConsumedBoxCount, "transferred Holy Box disappears");
        Check.Equal(10L, state.PrismCount,
            "12 prisms are consumed before two are transformed");

        await using var snapshotReader =
            new PostgresCharacterSnapshotReader(
                dataSource,
                itemContent.Templates);
        var projection = (await snapshotReader.ReadAsync(
            fixture.AccountId)).Character ??
            throw new InvalidOperationException(
                "Holy Suit fixture character snapshot is missing.");
        Check.True(
            projection.Progression.Revision ==
                state.ProgressionRevision &&
            projection.Loadout.InventoryRevision ==
                state.InventoryRevision,
            "character projection carries exact durable replay revisions");
    }

    private static async Task<HolySuitExecutionResult> ExecuteAsync(
        PostgresHolySuitCommandExecutor executor,
        Fixture fixture,
        CommandConnectionCorrelation connection,
        Guid operationId,
        HolySuitCommandOperation operation,
        int primarySlot = HolySuitCommandEnvelope.NoKitBagSlot,
        string primaryState = "[]",
        int secondarySlot = HolySuitCommandEnvelope.NoKitBagSlot,
        string secondaryState = "[]",
        long experience = 0,
        int prisms = 0)
    {
        if (!HolySuitCommandEnvelope.TryCreateCommand(
            HolySuitOperationIdentity.SecureClient(operationId),
            operation,
            HolySuitCommandEnvelope.SpartaNpcId,
            HolySuitCommandEnvelope.DialogIndex,
            primarySlot,
            primaryState,
            secondarySlot,
            secondaryState,
            experience,
            prisms,
            out var command))
        {
            throw new InvalidOperationException(
                "The Holy Suit integration command is invalid.");
        }
        return await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                HolySuitCommandEnvelope.CreateSecure(
                    fixture.Subject,
                    connection,
                    DateTimeOffset.UtcNow,
                    command)));
    }

    private static HolySuitExecutionReceipt Require(
        HolySuitExecutionResult result,
        HolySuitExecutionDisposition disposition,
        HolySuitCommandResultStatus status,
        string description)
    {
        Check.Equal((int)disposition, (int)result.Disposition,
            $"{description} disposition");
        var receipt = result.Receipt ??
            throw new InvalidOperationException(
                $"{description} returned no receipt.");
        Check.Equal((int)status, (int)receipt.Status,
            $"{description} status");
        return receipt;
    }

}
