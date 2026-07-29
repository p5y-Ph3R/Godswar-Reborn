using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresEquipmentForgeCommandIntegrationChecks
{
    private static async Task AssertSuccessAndFailedRollAsync(
        string connectionString)
    {
        var success = await CreateFixtureAsync(
            connectionString,
            "success",
            odds:
            [
                (2, 4232, 3, 2),
                (3, 4232, 4, 3)
            ]);
        await using var successSource =
            NpgsqlDataSource.Create(connectionString);
        var successRoll = new CountingRollSource(0);
        var successReceipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(successSource, successRoll.Next),
                success,
                Guid.NewGuid()),
            EquipmentForgeExecutionDisposition.Committed,
            "successful multistack forge");
        Check.True(
            successReceipt.Succeeded &&
            successReceipt.Roll == 0 &&
            successReceipt.MaterialType ==
                (int)EquipmentForgeOperation.Sapphire &&
            successReceipt.Materials.Length == 3 &&
            CompactItemEntry.Parse(
                successReceipt.EquipmentAfterCompactItemState).Quality == 2,
            "successful multistack receipt has exact outcome evidence");
        Check.Equal(1, successRoll.Calls, "successful forge samples once");

        var successState = await ReadStateAsync(
            connectionString,
            success);
        Check.True(
            successState.Silver == 999 &&
            successState.WalletRevision == 1 &&
            successState.InventoryRevision == 1 &&
            successState.AuditCount == 1 &&
            successState.InboxCount == 1 &&
            successState.CurrencyLedgerCount == 1 &&
            successState.InventoryLedgerCount == 4 &&
            successState.OutboxCount == 1,
            "successful forge commits wallet, changed items, ledgers, and outbox once");
        var successEquipment = await ReadSlotAsync(
            connectionString,
            success.CharacterId,
            0);
        var successPrimary = await ReadSlotAsync(
            connectionString,
            success.CharacterId,
            1);
        var successOddsA = await ReadSlotAsync(
            connectionString,
            success.CharacterId,
            2);
        var successOddsB = await ReadSlotAsync(
            connectionString,
            success.CharacterId,
            3);
        Check.True(
            successEquipment is { ItemId: 1000, Quality: 2, Stack: 1 } &&
            successPrimary is { ItemId: 4212, Stack: 1 } &&
            successOddsA is { ItemId: 4232, Stack: 1 } &&
            successOddsB is { ItemId: 4232, Stack: 1 },
            "successful forge persists equipment and every source-stack decrement");
        await AssertReconciledAsync(
            connectionString,
            success,
            "successful forge reconciles wallet and inventory ledgers");
        await AssertAuditEvidenceAsync(
            connectionString,
            success,
            successReceipt);

        var rubyBefore = Item(1000, 1) with
        {
            Attribute1 = 0,
            AttributeLevel1 = 7,
            Quality = 3,
            Grade = 4,
            Bound = 1,
            Exp = 123,
            HolySuitCode = 305
        };
        var ruby = await CreateFixtureAsync(
            connectionString,
            "ruby",
            equipment: rubyBefore,
            primaryItemId: 4200,
            primaryStack: 1);
        await using var rubySource =
            NpgsqlDataSource.Create(connectionString);
        var rubyReceipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(rubySource, () => 0),
                ruby,
                Guid.NewGuid()),
            EquipmentForgeExecutionDisposition.Committed,
            "Ruby item progression");
        Check.True(
            rubyReceipt.MaterialType ==
                (int)EquipmentForgeOperation.Ruby &&
            CompactItemEntry.Parse(
                rubyReceipt.EquipmentBeforeCompactItemState) ==
                    rubyBefore &&
            CompactItemEntry.Parse(
                rubyReceipt.EquipmentAfterCompactItemState) ==
                    rubyBefore with { Id = 1001 } &&
            await ReadSlotAsync(
                connectionString,
                ruby.CharacterId,
                0) is
                {
                    ItemId: 1001,
                    Quality: 3,
                    Grade: 4,
                    Stack: 1
                } &&
            await ReadSlotAsync(
                connectionString,
                ruby.CharacterId,
                1) is null,
            "Ruby replaces prop ID while preserving complete compact metadata");
        await AssertReconciledAsync(
            connectionString,
            ruby,
            "Ruby forge reconciles wallet and inventory ledgers");

        var failed = await CreateFixtureAsync(
            connectionString,
            "failed");
        await using var failedSource =
            NpgsqlDataSource.Create(connectionString);
        var failedRoll = new CountingRollSource(99);
        var failedReceipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(failedSource, failedRoll.Next),
                failed,
                Guid.NewGuid()),
            EquipmentForgeExecutionDisposition.Committed,
            "failed-roll forge");
        Check.True(
            failedReceipt.Status ==
                EquipmentForgeCommandResultStatus.FailedRoll &&
            failedReceipt.Roll == 99 &&
            string.Equals(
                failedReceipt.EquipmentBeforeCompactItemState,
                failedReceipt.EquipmentAfterCompactItemState,
                StringComparison.Ordinal) &&
            failedReceipt.Materials.Length == 1,
            "failed roll persists exact roll and consumes its material");
        var failedState = await ReadStateAsync(
            connectionString,
            failed);
        Check.True(
            failedState.Silver == 999 &&
            failedState.WalletRevision == 1 &&
            failedState.InventoryRevision == 1 &&
            failedState.CurrencyLedgerCount == 1 &&
            failedState.InventoryLedgerCount == 1 &&
            failedState.OutboxCount == 1,
            "failed roll charges silver but omits no-op equipment ledger");
        Check.True(
            await ReadSlotAsync(
                connectionString,
                failed.CharacterId,
                0) is { ItemId: 1000, Quality: 1, Stack: 1 } &&
            await ReadSlotAsync(
                connectionString,
                failed.CharacterId,
                1) is { ItemId: 4212, Stack: 1 },
            "failed roll leaves equipment unchanged and consumes primary");
        await AssertReconciledAsync(
            connectionString,
            failed,
            "failed roll reconciles wallet and inventory ledgers");
    }

    private static async Task AssertZeroSilverAndTerminalReplayAsync(
        string connectionString)
    {
        var attributed = Item(1000, 1) with
        {
            Attribute1 = 0,
            AttributeLevel1 = 1
        };
        var zeroCost = await CreateFixtureAsync(
            connectionString,
            "zero",
            equipment: attributed,
            primaryItemId: 4220,
            primaryStack: 1,
            silver: 1_000);
        await using var zeroSource =
            NpgsqlDataSource.Create(connectionString);
        var zeroReceipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(zeroSource, () => 0),
                zeroCost,
                Guid.NewGuid()),
            EquipmentForgeExecutionDisposition.Committed,
            "zero-silver grade forge");
        var zeroState = await ReadStateAsync(
            connectionString,
            zeroCost);
        Check.True(
            zeroReceipt.SilverSpent == 0 &&
            zeroReceipt.WalletRevision == 0 &&
            zeroReceipt.InventoryRevision == 1 &&
            zeroState.Silver == 1_000 &&
            zeroState.WalletRevision == 0 &&
            zeroState.InventoryRevision == 1 &&
            zeroState.CurrencyLedgerCount == 0 &&
            zeroState.InventoryLedgerCount == 2 &&
            zeroState.OutboxCount == 1,
            "zero-silver forge does not advance wallet or append zero ledger");
        Check.True(
            await ReadSlotAsync(
                connectionString,
                zeroCost.CharacterId,
                0) is { ItemId: 1000, Grade: 2, Stack: 1 } &&
            await ReadSlotAsync(
                connectionString,
                zeroCost.CharacterId,
                1) is null,
            "zero-cost Emerald persists G2 and deletes exact primary stack");
        await AssertReconciledAsync(
            connectionString,
            zeroCost,
            "zero-cost forge reconciles unchanged wallet and inventory");

        var rejected = await CreateFixtureAsync(
            connectionString,
            "reject");
        await using var rejectedSource =
            NpgsqlDataSource.Create(connectionString);
        var initialReceipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(rejectedSource, () => 0),
                rejected,
                Guid.NewGuid()),
            EquipmentForgeExecutionDisposition.Committed,
            "prior truthful forge activity");
        Check.True(
            initialReceipt.WalletRevision == 1 &&
            initialReceipt.InventoryRevision == 1,
            "prior forge establishes truthful ledger-backed revisions");
        var roll = new CountingRollSource(0);
        var executor = CreateExecutor(rejectedSource, roll.Next);
        var operationId = Guid.NewGuid();
        var rejectedReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                rejected,
                operationId),
            EquipmentForgeExecutionDisposition.TerminalRejected,
            "stale forge after prior inventory activity");
        Check.True(
            rejectedReceipt.Status ==
                EquipmentForgeCommandResultStatus.StaleSelection &&
            rejectedReceipt.WalletRevision == 1 &&
            rejectedReceipt.InventoryRevision == 1 &&
            rejectedReceipt.OutboxEventId is null &&
            roll.Calls == 0,
            "terminal rejection stores current revisions without sampling");
        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                rejected.Subject,
                operationId),
            EquipmentForgeExecutionDisposition.Duplicate,
            "terminal forge replay");
        AssertReceiptsEqual(
            rejectedReceipt,
            replay,
            "terminal replay returns the exact stored receipt");
        var rejectedState = await ReadStateAsync(
            connectionString,
            rejected);
        Check.True(
            rejectedState.AuditCount == 2 &&
            rejectedState.InboxCount == 2 &&
            rejectedState.TerminalRejectedCount == 1 &&
            rejectedState.DuplicateCount == 1 &&
            rejectedState.CurrencyLedgerCount == 1 &&
            rejectedState.InventoryLedgerCount == 2 &&
            rejectedState.OutboxCount == 1,
            "terminal rejection adds audit/inbox only after prior commit");
        await AssertReconciledAsync(
            connectionString,
            rejected,
            "prior activity and terminal rejection remain reconciled");
    }

    private static async Task AssertAuditEvidenceAsync(
        string connectionString,
        ForgeFixture fixture,
        EquipmentForgeExecutionReceipt receipt)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (detail_payload ->> 'roll')::integer,
                (detail_payload ->> 'probability')::integer,
                (detail_payload ->> 'materialType')::integer,
                (detail_payload ->> 'silverSpent')::integer
            FROM public.command_audit
            WHERE principal_key = @principalKey
              AND aggregate_key = @aggregateKey
              AND command_family = @commandFamily;
            """,
            connection);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            EquipmentForgePersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            EquipmentForgePersistenceCodec.CommandFamilyCode);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt32(0) == receipt.Roll &&
            reader.GetInt32(1) == receipt.Probability &&
            reader.GetInt32(2) == receipt.MaterialType &&
            reader.GetInt32(3) == receipt.SilverSpent,
            "forge audit stores exact roll, chance, operation, and cost");
    }
}
