using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private const uint MountItemId = 14224;

    private static async Task
        AssertMountGrantCommitReplayAndConflictAsync(
            string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "mount");
        var operationId = Guid.NewGuid();
        var envelope = CreateMountEnvelope(fixture, operationId);

        DeveloperItemGrantExecutionResult first;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(source)
                .ExecuteAsync(envelope);
        }

        var committed = RequireReceipt(
            first,
            DeveloperItemGrantExecutionDisposition.Committed,
            "first developer-mount grant");
        Check.True(
            committed.CharacterId == fixture.CharacterId &&
            committed.ItemId == MountItemId &&
            committed.GrantedQuantity == 1 &&
            committed.InventoryRevision == 1,
            "mount grant returns the authoritative revision");
        AssertMountState(
            await ReadMountStateAsync(connectionString, fixture),
            expectedDuplicateCount: 0,
            expectedConflictCount: 0,
            "mount grant commits one bound stack-one item and evidence");

        DeveloperItemGrantExecutionResult retry;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await CreateExecutor(source).ExecuteAsync(
                CreateMountEnvelope(
                    fixture,
                    operationId,
                    connectionId: Guid.NewGuid()));
        }

        var duplicate = RequireReceipt(
            retry,
            DeveloperItemGrantExecutionDisposition.Duplicate,
            "exact developer-mount retry");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "mount replay returns the canonical durable receipt");
        AssertMountState(
            await ReadMountStateAsync(connectionString, fixture),
            expectedDuplicateCount: 1,
            expectedConflictCount: 0,
            "mount replay creates no duplicate item or durable event");

        DeveloperItemGrantExecutionResult conflict;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            conflict = await CreateExecutor(source).ExecuteAsync(
                CreateDifferentItemEnvelope(
                    fixture,
                    operationId,
                    connectionId: Guid.NewGuid()));
        }

        Check.True(
            conflict.Disposition ==
                DeveloperItemGrantExecutionDisposition
                    .RequestHashConflict &&
            conflict.Receipt is null,
            "same mount operation UUID with another item conflicts");
        AssertMountState(
            await ReadMountStateAsync(connectionString, fixture),
            expectedDuplicateCount: 1,
            expectedConflictCount: 1,
            "mount request conflict changes only bounded evidence");
    }

    private static CommandEnvelope<DeveloperItemGrantCommand>
        CreateMountEnvelope(
            GrantFixture fixture,
            Guid clientOperationId,
            Guid? connectionId = null) =>
        CreateItemEnvelope(
            fixture,
            MountItemId,
            quantity: 1,
            clientOperationId,
            connectionId);

    private static CommandEnvelope<DeveloperItemGrantCommand>
        CreateDifferentItemEnvelope(
            GrantFixture fixture,
            Guid clientOperationId,
            Guid? connectionId = null) =>
        CreateItemEnvelope(
            fixture,
            MaterialItemId,
            quantity: 1,
            clientOperationId,
            connectionId);

    private static CommandEnvelope<DeveloperItemGrantCommand>
        CreateItemEnvelope(
            GrantFixture fixture,
            uint itemId,
            int quantity,
            Guid clientOperationId,
            Guid? connectionId)
    {
        if (!DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                itemId,
                quantity,
                clientOperationId,
                out var command))
        {
            throw new InvalidOperationException(
                "The mount fixture requested an invalid grant.");
        }

        return DeveloperItemGrantCommandEnvelope.Create(
            new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId),
            new CommandConnectionCorrelation(
                connectionId ?? Guid.NewGuid(),
                CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command);
    }

    private static async Task<MountGrantState> ReadMountStateAsync(
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
                (
                    SELECT count(*)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @itemId
                      AND item_row.bound = 1
                      AND item_row.stack = 1
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.command_audit audit
                    WHERE audit.principal_type = @principalType
                      AND audit.principal_key = @principalKey
                      AND audit.aggregate_type = @aggregateType
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @commandFamily
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.reason_code = 'developer_mount_grant'
                      AND ledger.mutation_kind = 'add'
                      AND (ledger.after_state ->> 'prop_id')::integer =
                          @itemId
                      AND (ledger.after_state ->> 'bound')::integer = 1
                      AND (ledger.after_state ->> 'stack')::integer = 1
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = @aggregateType
                      AND outbox.aggregate_key = @aggregateKey
                      AND outbox.event_type = @eventType
                ),
                COALESCE((
                    SELECT max(inbox.duplicate_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer,
                COALESCE((
                    SELECT max(inbox.request_conflict_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer,
                reconciliation.is_reconciled
            FROM public.character_base character_row
            JOIN public.character_inventory_reconciliation reconciliation
              ON reconciliation.character_id = character_row.id
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId;
            """,
            connection);
        AddMountStateParameters(command, fixture);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The developer-mount fixture disappeared.");
        }

        return new MountGrantState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetBoolean(9));
    }

    private static void AddMountStateParameters(
        NpgsqlCommand command,
        GrantFixture fixture)
    {
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)MountItemId));
        command.Parameters.AddWithValue(
            "principalType",
            DeveloperItemGrantPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            DeveloperItemGrantPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            DeveloperItemGrantPersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            DeveloperItemGrantPersistenceCodec.CommandFamily);
        command.Parameters.AddWithValue(
            "eventType",
            DeveloperItemGrantPersistenceCodec.EventType);
    }

    private static void AssertMountState(
        MountGrantState state,
        int expectedDuplicateCount,
        int expectedConflictCount,
        string description)
    {
        Check.True(
            state.InventoryRevision == 1 &&
            state.ValidMountCount == 1 &&
            state.TotalItemCount == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.MountLedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == expectedDuplicateCount &&
            state.RequestConflictCount == expectedConflictCount &&
            state.IsReconciled,
            description);
    }

    private sealed record MountGrantState(
        long InventoryRevision,
        long ValidMountCount,
        long TotalItemCount,
        long AuditCount,
        long InboxCount,
        long MountLedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int RequestConflictCount,
        bool IsReconciled);
}
