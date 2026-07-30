using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresGearMentorMaterialConversionIntegrationChecks
{
    private static async Task<ConversionFixture>
        CreateFixtureAsync(
            string connectionString,
            string scenario,
            CommandFamily family,
            uint sourceItemId,
            short sourceStack,
            uint outputItemId,
            int outputQuantity,
            bool isBound = true,
            short selectedSlot = DefaultSelectedSlot,
            bool includeSource = true,
            short? expectedSourceStack = null,
            short? existingOutputStack = null,
            bool fillRemainingBag = false,
            uint? additionalItemId = null,
            short additionalItemStack = 1,
            short additionalItemSlot = 1,
            short additionalItemBound = 1)
    {
        if (sourceStack is <= 0 or > 9999 ||
            expectedSourceStack is <= 0 or > 9999 ||
            existingOutputStack is <= 0 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStack));
        }
        if (selectedSlot is < 0 or > 95 ||
            existingOutputStack.HasValue && fillRemainingBag)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedSlot));
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario =
            scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09mc_{shortScenario}_{token}";
        var characterName = $"MC{shortScenario}{token}";
        var bound = isBound ? (short)1 : (short)0;
        var expectedState = CompactItemEntry.Parse(
            $"[{sourceItemId},,,,,,1,1,{bound}," +
            $"{expectedSourceStack ?? sourceStack},0]")
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

        if (includeSource)
        {
            await InsertFixtureItemAsync(
                connection,
                transaction,
                characterId,
                selectedSlot,
                sourceItemId,
                sourceStack,
                bound);
        }

        if (existingOutputStack.HasValue)
        {
            var outputSlot = selectedSlot == 1 ? (short)2 : (short)1;
            await InsertFixtureItemAsync(
                connection,
                transaction,
                characterId,
                outputSlot,
                outputItemId,
                existingOutputStack.Value,
                bound);
        }

        if (additionalItemId.HasValue)
        {
            await InsertFixtureItemAsync(
                connection,
                transaction,
                characterId,
                additionalItemSlot,
                additionalItemId.Value,
                additionalItemStack,
                additionalItemBound);
        }

        if (fillRemainingBag)
        {
            for (short slot = 0; slot < 96; slot++)
            {
                if (slot == selectedSlot)
                {
                    continue;
                }

                await InsertFixtureItemAsync(
                    connection,
                    transaction,
                    characterId,
                    slot,
                    itemId: 1000,
                    stack: 1,
                    bound: 0);
            }
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "material-conversion fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();

        return new ConversionFixture(
            accountId,
            characterId,
            username,
            family,
            selectedSlot,
            expectedState,
            sourceItemId,
            sourceStack,
            outputItemId,
            outputQuantity,
            isBound);
    }

    private static async Task InsertFixtureItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short slot,
        uint itemId,
        short stack,
        short bound)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp,
                holy_suit_code
            )
            VALUES (
                @characterId,
                1,
                @slot,
                @itemId,
                1,
                1,
                @bound,
                @stack,
                0,
                0
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)itemId));
        command.Parameters.AddWithValue("bound", bound);
        command.Parameters.AddWithValue("stack", stack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"material fixture item {itemId} inserted");
    }

    private static async Task<ConversionDurableState>
        ReadStateAsync(
            string connectionString,
            ConversionFixture fixture)
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
                      AND item_row.prop_id = @sourceItemId
                ), 0)::bigint,
                COALESCE((
                    SELECT sum(item_row.stack)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @outputItemId
                ), 0)::bigint,
                COALESCE((
                    SELECT max(item_row.bound)
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @outputItemId
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
                    SELECT count(*) FILTER (
                        WHERE ledger.mutation_kind = 'add')::bigint
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.reason_code = @ledgerReason
                ), 0)::bigint,
                COALESCE((
                    SELECT count(*) FILTER (
                        WHERE ledger.mutation_kind = 'update')::bigint
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.reason_code = @ledgerReason
                ), 0)::bigint,
                COALESCE((
                    SELECT count(*) FILTER (
                        WHERE ledger.mutation_kind = 'delete')::bigint
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.reason_code = @ledgerReason
                ), 0)::bigint,
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
                "The material-conversion fixture disappeared.");
        }

        return new ConversionDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt16(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetBoolean(16));
    }

    private static void AddStateParameters(
        NpgsqlCommand command,
        ConversionFixture fixture)
    {
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "sourceItemId",
            checked((int)fixture.SourceItemId));
        command.Parameters.AddWithValue(
            "outputItemId",
            checked((int)fixture.OutputItemId));
        command.Parameters.AddWithValue(
            "principalType",
            GearMentorMaterialConversionPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            GearMentorMaterialConversionPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            GearMentorMaterialConversionPersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            GearMentorMaterialConversionPersistenceCodec
                .CommandFamilyCode(fixture.Family));
        command.Parameters.AddWithValue(
            "ledgerReason",
            GearMentorMaterialConversionPersistenceCodec
                .LedgerReasonCode(fixture.Family));
        command.Parameters.AddWithValue(
            "eventType",
            GearMentorMaterialConversionPersistenceCodec.EventType(
                fixture.Family));
    }

    private sealed record ConversionFixture(
        int AccountId,
        int CharacterId,
        string Username,
        CommandFamily Family,
        short SelectedSlot,
        string ExpectedSelectedState,
        uint SourceItemId,
        short InitialSourceStack,
        uint OutputItemId,
        int OutputQuantity,
        bool IsBound);

    private sealed record ConversionDurableState(
        long InventoryRevision,
        long SourceQuantity,
        long OutputQuantity,
        short OutputBound,
        long TotalBagItemCount,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        long CommittedInboxCount,
        long RejectedInboxCount,
        long AddLedgerCount,
        long UpdateLedgerCount,
        long DeleteLedgerCount,
        bool IsReconciled);
}
