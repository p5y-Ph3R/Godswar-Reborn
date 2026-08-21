using System.Globalization;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private static async Task<StoneFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        short dustStack = RecipeDustQuantity,
        bool isBound = true,
        uint selectedItemId = DustItemId,
        bool includeSelectedItem = true,
        short? expectedSelectedStack = null,
        short? existingStoneStack = null,
        bool fillRemainingBag = false,
        bool captureBaseline = true)
    {
        if (dustStack is <= 0 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(dustStack));
        }

        if (expectedSelectedStack is <= 0 or > 9999 ||
            existingStoneStack is <= 0 or > 9999 ||
            existingStoneStack.HasValue && fillRemainingBag)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSelectedStack));
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario =
            scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09_{shortScenario}_{token}";
        var characterName = $"B9{shortScenario}{token}";
        var bound = isBound ? (short)1 : (short)0;
        var expectedState = CompactItemEntry.Parse(
            $"[{selectedItemId},,,,,,1,1,{bound}," +
            $"{expectedSelectedStack ?? dustStack},0]")
            .ToCompactString();

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The fixture account insert returned no identity."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                server_id,
                name,
                camp,
                profession,
                fighter_job_lv,
                "Money",
                "Stone",
                inventory_revision
            )
            VALUES (
                @accountId,
                1,
                @name,
                1,
                0,
                80,
                1000,
                100,
                0
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("name", characterName);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The fixture character insert returned no identity."));
        }

        if (includeSelectedItem)
        {
            await InsertFixtureItemAsync(
                connection,
                transaction,
                characterId,
                SelectedKitBagSlot,
                selectedItemId,
                dustStack,
                bound);
        }

        if (existingStoneStack.HasValue)
        {
            await InsertFixtureItemAsync(
                connection,
                transaction,
                characterId,
                slot: 1,
                AttributeStoneItemId,
                existingStoneStack.Value,
                bound);
        }

        if (fillRemainingBag)
        {
            await FillRemainingKitBagAsync(
                connection,
                transaction,
                characterId);
        }

        if (captureBaseline)
        {
            Check.True(
                await PostgresCharacterEconomyBaseline.EnsureAsync(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    commandTimeoutSeconds: 30,
                    CancellationToken.None),
                "Make Attribute Stone fixture captures an economy baseline");
        }

        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();

        return new StoneFixture(
            accountId,
            characterId,
            expectedState,
            isBound);
    }

    private static async Task<StoneDurableState> ReadStateAsync(
        string connectionString,
        StoneFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
                COALESCE((
                    SELECT sum(item_row.stack)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @dustItemId
                ), 0)::bigint,
                COALESCE((
                    SELECT sum(item_row.stack)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @stoneItemId
                ), 0)::bigint,
                (
                    SELECT count(*)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @stoneItemId
                ),
                COALESCE((
                    SELECT max(item_row.bound)
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @stoneItemId
                ), -1)::smallint,
                COALESCE((
                    SELECT min(item_row.slot_index)
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @stoneItemId
                ), -1)::smallint,
                (
                    SELECT count(*)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
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
                      AND ledger.reason_code = @ledgerReason
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.reason_code = @ledgerReason
                      AND (ledger.before_state ->> 'prop_id')::integer =
                          @dustItemId
                      AND (ledger.after_state ->> 'prop_id')::integer =
                          @stoneItemId
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
                (
                    SELECT count(*)::bigint
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                      AND inbox.result_code = 'committed'
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                      AND inbox.result_code = 'terminal_rejected'
                ),
                COALESCE((
                    SELECT reconciliation.is_reconciled
                    FROM public.character_inventory_reconciliation
                        reconciliation
                    WHERE reconciliation.character_id = @characterId
                ), false)
            FROM public.character_base character_row
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId;
            """,
            connection);
        AddStateParameters(command, fixture);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Make Attribute Stone fixture disappeared.");
        }

        return new StoneDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt16(4),
            reader.GetInt16(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetBoolean(16));
    }

    private static void AddStateParameters(
        NpgsqlCommand command,
        StoneFixture fixture)
    {
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "dustItemId",
            checked((int)DustItemId));
        command.Parameters.AddWithValue(
            "stoneItemId",
            checked((int)AttributeStoneItemId));
        command.Parameters.AddWithValue(
            "principalType",
            MakeAttributeStonePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            MakeAttributeStonePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            MakeAttributeStonePersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            MakeAttributeStonePersistenceCodec.CommandFamily);
        command.Parameters.AddWithValue(
            "ledgerReason",
            MakeAttributeStonePersistenceCodec.LedgerReasonCode);
        command.Parameters.AddWithValue(
            "eventType",
            MakeAttributeStonePersistenceCodec.EventType);
    }

    private sealed record StoneFixture(
        int AccountId,
        int CharacterId,
        string ExpectedSelectedState,
        bool IsBound);

    private sealed record StoneDurableState(
        long InventoryRevision,
        long DustQuantity,
        long StoneQuantity,
        long StoneItemCount,
        short StoneBound,
        short StoneSlot,
        long TotalBagItemCount,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long RecipeLedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        long CommittedInboxCount,
        long RejectedInboxCount,
        bool IsReconciled);
}
