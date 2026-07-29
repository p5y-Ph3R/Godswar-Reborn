using System.Globalization;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private static async Task<GrantFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        bool fillKitBag = false,
        bool createEconomyBaseline = true,
        short? existingGrantStack = null,
        long inventoryRevision = 0)
    {
        if (fillKitBag && existingGrantStack.HasValue)
        {
            throw new ArgumentException(
                "A fixture cannot be both full and partially stacked.",
                nameof(existingGrantStack));
        }

        if (createEconomyBaseline && inventoryRevision != 0)
        {
            throw new ArgumentException(
                "A test baseline can only represent revision zero.",
                nameof(inventoryRevision));
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09_{shortScenario}_{token}";
        var characterName = $"B9{shortScenario}{token}";

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
                @inventoryRevision
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("name", characterName);
            character.Parameters.AddWithValue(
                "inventoryRevision",
                inventoryRevision);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The fixture character insert returned no identity."));
        }

        if (fillKitBag)
        {
            await FillKitBagAsync(
                connection,
                transaction,
                characterId);
        }

        if (existingGrantStack.HasValue)
        {
            await InsertPartialGrantStackAsync(
                connection,
                transaction,
                characterId,
                existingGrantStack.Value);
        }

        if (createEconomyBaseline)
        {
            await InsertEconomyBaselineAsync(
                connection,
                transaction,
                accountId,
                characterId);
        }
        await transaction.CommitAsync();

        return new GrantFixture(
            accountId,
            characterId,
            username,
            characterName);
    }

    private static async Task InsertPartialGrantStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short stack)
    {
        if (stack is <= 0 or >= 99)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stack),
                "The cutover fixture requires a partial stack.");
        }

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
                0,
                @itemId,
                1,
                1,
                0,
                @stack,
                0,
                0
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)MaterialItemId));
        command.Parameters.AddWithValue("stack", stack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "cutover fixture inserts one partial material stack");
    }

    private static async Task FillKitBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId)
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
            SELECT
                @characterId,
                1,
                slot::smallint,
                4200,
                1,
                1,
                0,
                1,
                0,
                0
            FROM generate_series(0, 95) AS slot;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            96,
            await command.ExecuteNonQueryAsync(),
            "full-kitbag fixture inserts every authoritative slot");
    }

    private static async Task DeleteOneFixtureBagItemAsync(
        string connectionString,
        int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM public.character_items
            WHERE id = (
                SELECT id
                FROM public.character_items
                WHERE user_id = @characterId
                  AND item_location = 1
                ORDER BY slot_index DESC
                LIMIT 1
            );
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "capacity replay fixture opens one bag slot");
    }

    private static async Task InsertEconomyBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId)
    {
        await using (var baseline = new NpgsqlCommand(
            """
            INSERT INTO public.character_economy_baseline (
                character_id,
                account_id,
                wallet_revision,
                inventory_revision,
                silver,
                gold,
                item_count,
                baseline_source
            )
            SELECT
                character_row.id,
                character_row.account_id,
                character_row.wallet_revision,
                character_row.inventory_revision,
                character_row."Money"::bigint,
                character_row."Stone"::bigint,
                count(item_row.id)::integer,
                'test_b09'
            FROM public.character_base character_row
            LEFT JOIN public.character_items item_row
              ON item_row.user_id = character_row.id
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId
            GROUP BY character_row.id;
            """,
            connection,
            transaction))
        {
            baseline.Parameters.AddWithValue("accountId", accountId);
            baseline.Parameters.AddWithValue(
                "characterId",
                characterId);
            Check.Equal(
                1,
                await baseline.ExecuteNonQueryAsync(),
                "fixture economy baseline inserts one character");
        }

        await using var snapshots = new NpgsqlCommand(
            """
            INSERT INTO public.character_inventory_baseline_items (
                character_id,
                account_id,
                item_instance_id,
                item_location,
                slot_index,
                prop_id,
                state_contract_version,
                item_state
            )
            SELECT
                item_row.user_id,
                @accountId,
                item_row.id,
                item_row.item_location,
                item_row.slot_index,
                item_row.prop_id,
                1,
                to_jsonb(item_row)
            FROM public.character_items item_row
            WHERE item_row.user_id = @characterId;
            """,
            connection,
            transaction);
        snapshots.Parameters.AddWithValue("accountId", accountId);
        snapshots.Parameters.AddWithValue("characterId", characterId);
        _ = await snapshots.ExecuteNonQueryAsync();
    }

    private static async Task<GrantDurableState> ReadStateAsync(
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
                    SELECT sum(item_row.stack)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @itemId
                ), 0)::bigint,
                (
                    SELECT count(*)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id = @itemId
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
                      AND ledger.reason_code =
                          'developer_material_grant'
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
        AddStateParameters(command, fixture);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The developer-item grant fixture disappeared.");
        }

        return new GrantDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetBoolean(10));
    }

    private static void AddStateParameters(
        NpgsqlCommand command,
        GrantFixture fixture)
    {
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)MaterialItemId));
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

    private sealed record GrantFixture(
        int AccountId,
        int CharacterId,
        string Username,
        string CharacterName);

    private sealed record GrantDurableState(
        long InventoryRevision,
        long GrantedQuantity,
        long GrantedItemCount,
        long TotalItemCount,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int RequestConflictCount,
        bool IsReconciled);
}
