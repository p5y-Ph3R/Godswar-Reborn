using System.Diagnostics;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private const short CutoverStackBefore = 10;

    private static async Task AssertLazyRuntimeCutoverAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "cutover",
            createEconomyBaseline: false,
            existingGrantStack: CutoverStackBefore);
        var before = await ReadCutoverEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            before.InventoryRevision == 0 &&
            before.CurrentStack == CutoverStackBefore &&
            before.BaselineCount == 0 &&
            before.SnapshotCount == 0 &&
            before.LedgerCount == 0 &&
            before.InboxCount == 0 &&
            !before.IsReconciled,
            "legacy cutover fixture starts at revision zero without " +
            "economy evidence");

        DeveloperItemGrantExecutionResult result;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            result = await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(fixture, Guid.NewGuid()));
        }

        var receipt = RequireReceipt(
            result,
            DeveloperItemGrantExecutionDisposition.Committed,
            "lazy runtime-cutover grant");
        Check.True(
            receipt.InventoryRevision == 1 &&
            receipt.GrantedQuantity == GrantQuantity,
            "lazy cutover grant commits revision one");

        var after = await ReadCutoverEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            after.InventoryRevision == 1 &&
            after.CurrentStack ==
                CutoverStackBefore + GrantQuantity &&
            after.BaselineCount == 1 &&
            after.BaselineInventoryRevision == 0 &&
            after.BaselineItemCount == 1 &&
            string.Equals(
                after.BaselineSource,
                "runtime_cutover",
                StringComparison.Ordinal) &&
            after.SnapshotCount == 1 &&
            after.SnapshotStack == CutoverStackBefore &&
            after.LedgerCount == 1 &&
            string.Equals(
                after.MutationKind,
                "update",
                StringComparison.Ordinal) &&
            after.LedgerRevision == 1 &&
            after.LedgerBeforeStack == CutoverStackBefore &&
            after.LedgerAfterStack ==
                CutoverStackBefore + GrantQuantity &&
            after.AuditCount == 1 &&
            after.InboxCount == 1 &&
            after.OutboxCount == 1 &&
            after.ExpectedItemCount == 1 &&
            after.CurrentItemCount == 1 &&
            after.MismatchedItemCount == 0 &&
            after.IsReconciled,
            "first tokenized mutation snapshots the legacy partial stack " +
            "before updating it and leaves reconciliation clean");
    }

    private static async Task AssertConcurrentLegacyDeleteIsFencedAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "delete",
            createEconomyBaseline: false,
            existingGrantStack: CutoverStackBefore);
        var probe = new PausingGrantProbe(
            PostgresDeveloperItemGrantCommandStage.AuditInserted);

        await using var grantSource =
            NpgsqlDataSource.Create(connectionString);
        var grantTask = CreateExecutor(grantSource, probe).ExecuteAsync(
            CreateEnvelope(fixture, Guid.NewGuid()));
        await probe.WaitUntilReachedAsync();

        await using var deleteConnection =
            new NpgsqlConnection(connectionString);
        await deleteConnection.OpenAsync();
        await using var deleteTransaction =
            await deleteConnection.BeginTransactionAsync();
        var deleteBackendId = await ReadBackendIdAsync(
            deleteConnection,
            deleteTransaction);
        await using var deleteCommand = new NpgsqlCommand(
            """
            DELETE FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @itemId;
            """,
            deleteConnection,
            deleteTransaction);
        deleteCommand.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        deleteCommand.Parameters.AddWithValue(
            "itemId",
            checked((int)MaterialItemId));
        var deleteTask = deleteCommand.ExecuteNonQueryAsync();

        try
        {
            Check.True(
                await WaitForBlockedItemTableLockAsync(
                    connectionString,
                    deleteBackendId,
                    deleteTask),
                "legacy DELETE waits on the cutover table-lock fence");
            Check.True(
                !deleteTask.IsCompleted && !grantTask.IsCompleted,
                "legacy DELETE cannot execute or commit while the " +
                "baselining grant transaction owns the fence");

            probe.Release();
            var grantReceipt = RequireReceipt(
                await grantTask.WaitAsync(TimeSpan.FromSeconds(10)),
                DeveloperItemGrantExecutionDisposition.Committed,
                "fenced runtime-cutover grant");
            Check.True(
                grantReceipt.InventoryRevision == 1,
                "fenced grant commits before the legacy DELETE proceeds");

            Check.Equal(
                1,
                await deleteTask.WaitAsync(TimeSpan.FromSeconds(10)),
                "serialized legacy DELETE affects the material row");

            var beforeDeleteCommit = await ReadCutoverEvidenceAsync(
                connectionString,
                fixture);
            Check.True(
                beforeDeleteCommit.CurrentStack ==
                    CutoverStackBefore + GrantQuantity &&
                beforeDeleteCommit.ExpectedItemCount == 1 &&
                beforeDeleteCommit.CurrentItemCount == 1 &&
                beforeDeleteCommit.MismatchedItemCount == 0 &&
                beforeDeleteCommit.IsReconciled,
                "grant is internally reconciled while the serialized " +
                "legacy DELETE remains uncommitted");

            await deleteTransaction.CommitAsync();
            var afterDeleteCommit = await ReadCutoverEvidenceAsync(
                connectionString,
                fixture);
            Check.True(
                afterDeleteCommit.InventoryRevision == 1 &&
                afterDeleteCommit.CurrentStack == 0 &&
                afterDeleteCommit.BaselineCount == 1 &&
                afterDeleteCommit.SnapshotStack ==
                    CutoverStackBefore &&
                afterDeleteCommit.LedgerCount == 1 &&
                afterDeleteCommit.LedgerBeforeStack ==
                    CutoverStackBefore &&
                afterDeleteCommit.LedgerAfterStack ==
                    CutoverStackBefore + GrantQuantity &&
                afterDeleteCommit.AuditCount == 1 &&
                afterDeleteCommit.InboxCount == 1 &&
                afterDeleteCommit.OutboxCount == 1 &&
                afterDeleteCommit.ExpectedItemCount == 1 &&
                afterDeleteCommit.CurrentItemCount == 0 &&
                afterDeleteCommit.MismatchedItemCount == 1 &&
                !afterDeleteCommit.IsReconciled,
                "post-fence unledgered legacy DELETE is serialized after " +
                "the grant and exposed explicitly as reconciliation drift");
        }
        finally
        {
            probe.Release();
        }
    }

    private static async Task
        AssertUnbaselinedAdvancedRevisionFailsClosedAsync(
            string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "ahead",
            createEconomyBaseline: false,
            inventoryRevision: 1);

        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(source).ExecuteAsync(
            CreateEnvelope(fixture, Guid.NewGuid()));
        Check.True(
            result.Disposition ==
                DeveloperItemGrantExecutionDisposition
                    .PreconditionFailed &&
            result.Receipt is null,
            "unbaselined nonzero inventory revision fails closed");

        var state = await ReadCutoverEvidenceAsync(
            connectionString,
            fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.CurrentStack == 0 &&
            state.BaselineCount == 0 &&
            state.SnapshotCount == 0 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            !state.IsReconciled,
            "advanced unbaselined character creates no baseline, inbox, " +
            "mutation, ledger, audit, or outbox");
    }

    private static async Task<CutoverEvidence> ReadCutoverEvidenceAsync(
        string connectionString,
        GrantFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
                COALESCE((
                    SELECT item_row.stack::integer
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @itemId
                    ORDER BY item_row.id
                    LIMIT 1
                ), 0),
                (
                    SELECT count(*)::integer
                    FROM public.character_economy_baseline baseline
                    WHERE baseline.character_id = @characterId
                      AND baseline.account_id = @accountId
                ),
                COALESCE((
                    SELECT baseline.inventory_revision
                    FROM public.character_economy_baseline baseline
                    WHERE baseline.character_id = @characterId
                      AND baseline.account_id = @accountId
                ), -1),
                COALESCE((
                    SELECT baseline.item_count
                    FROM public.character_economy_baseline baseline
                    WHERE baseline.character_id = @characterId
                      AND baseline.account_id = @accountId
                ), -1),
                COALESCE((
                    SELECT baseline.baseline_source
                    FROM public.character_economy_baseline baseline
                    WHERE baseline.character_id = @characterId
                      AND baseline.account_id = @accountId
                ), ''),
                (
                    SELECT count(*)::integer
                    FROM public.character_inventory_baseline_items snapshot
                    WHERE snapshot.character_id = @characterId
                      AND snapshot.account_id = @accountId
                ),
                COALESCE((
                    SELECT (snapshot.item_state ->> 'stack')::integer
                    FROM public.character_inventory_baseline_items snapshot
                    WHERE snapshot.character_id = @characterId
                      AND snapshot.account_id = @accountId
                    ORDER BY snapshot.item_instance_id
                    LIMIT 1
                ), 0),
                (
                    SELECT count(*)::integer
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.account_id = @accountId
                ),
                COALESCE((
                    SELECT ledger.mutation_kind
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.account_id = @accountId
                    ORDER BY ledger.id
                    LIMIT 1
                ), ''),
                COALESCE((
                    SELECT ledger.inventory_revision
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.account_id = @accountId
                    ORDER BY ledger.id
                    LIMIT 1
                ), 0),
                COALESCE((
                    SELECT (ledger.before_state ->> 'stack')::integer
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.account_id = @accountId
                    ORDER BY ledger.id
                    LIMIT 1
                ), 0),
                COALESCE((
                    SELECT (ledger.after_state ->> 'stack')::integer
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.account_id = @accountId
                    ORDER BY ledger.id
                    LIMIT 1
                ), 0),
                (
                    SELECT count(*)::integer
                    FROM public.command_audit audit
                    WHERE audit.principal_type = @principalType
                      AND audit.principal_key = @principalKey
                      AND audit.aggregate_type = @aggregateType
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @commandFamily
                ),
                (
                    SELECT count(*)::integer
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ),
                (
                    SELECT count(*)::integer
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = @aggregateType
                      AND outbox.aggregate_key = @aggregateKey
                      AND outbox.event_type = @eventType
                ),
                reconciliation.expected_item_count,
                reconciliation.current_item_count,
                reconciliation.mismatched_item_count,
                reconciliation.is_reconciled
            FROM public.character_base character_row
            JOIN public.character_inventory_reconciliation reconciliation
              ON reconciliation.character_id = character_row.id
            WHERE character_row.id = @characterId
              AND character_row.account_id = @accountId;
            """,
            connection);
        AddStateParameters(command, fixture);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The lazy-cutover fixture disappeared.");
        }

        return new CutoverEvidence(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.GetInt64(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.GetBoolean(19));
    }

    private static async Task<int> ReadBackendIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_backend_pid();",
            connection,
            transaction);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "PostgreSQL returned no backend process ID."));
    }

    private static async Task<bool> WaitForBlockedItemTableLockAsync(
        string connectionString,
        int backendId,
        Task deleteTask)
    {
        var timeout = Stopwatch.StartNew();
        await using var observer =
            new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_locks item_lock
                WHERE item_lock.pid = @backendId
                  AND item_lock.locktype = 'relation'
                  AND item_lock.relation =
                      'public.character_items'::regclass
                  AND item_lock.mode = 'RowExclusiveLock'
                  AND NOT item_lock.granted
            );
            """,
            observer);
        command.Parameters.AddWithValue("backendId", backendId);

        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (deleteTask.IsCompleted)
            {
                return false;
            }

            if (await command.ExecuteScalarAsync() is true)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private sealed class PausingGrantProbe(
        PostgresDeveloperItemGrantCommandStage stage) :
        IPostgresDeveloperItemGrantCommandProbe
    {
        private readonly TaskCompletionSource<bool> _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ReachedAsync(
            PostgresDeveloperItemGrantCommandStage reachedStage,
            CancellationToken cancellationToken)
        {
            if (reachedStage != stage)
            {
                return ValueTask.CompletedTask;
            }

            _reached.TrySetResult(true);
            return new ValueTask(
                _release.Task.WaitAsync(cancellationToken));
        }

        public Task WaitUntilReachedAsync() =>
            _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() =>
            _release.TrySetResult(true);
    }

    private sealed record CutoverEvidence(
        long InventoryRevision,
        int CurrentStack,
        int BaselineCount,
        long BaselineInventoryRevision,
        int BaselineItemCount,
        string BaselineSource,
        int SnapshotCount,
        int SnapshotStack,
        int LedgerCount,
        string MutationKind,
        long LedgerRevision,
        int LedgerBeforeStack,
        int LedgerAfterStack,
        int AuditCount,
        int InboxCount,
        int OutboxCount,
        int ExpectedItemCount,
        int CurrentItemCount,
        int MismatchedItemCount,
        bool IsReconciled);
}
