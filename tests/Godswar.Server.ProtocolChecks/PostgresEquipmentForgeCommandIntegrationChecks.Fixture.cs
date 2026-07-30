using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresEquipmentForgeCommandIntegrationChecks
{
    private static async Task<ForgeFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        CompactItemEntry? equipment = null,
        uint primaryItemId = 4212,
        short primaryStack = 2,
        IReadOnlyList<(short Slot, uint ItemId, short Stack, int Quantity)>?
            odds = null,
        int silver = 1_000)
    {
        equipment ??= Item(1000, stack: 1);
        odds ??= [];
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09ef_{shortScenario}_{token}";
        var characterName = $"EF{shortScenario}{token}";
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
                    "The forge fixture account insert returned no identity."));
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
                wallet_revision,
                inventory_revision
            )
            VALUES (
                @accountId,
                @name,
                1,
                0,
                80,
                @silver,
                100,
                0,
                0
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("name", characterName);
            character.Parameters.AddWithValue("silver", silver);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The forge fixture character insert returned no identity."));
        }

        var primary = Item(primaryItemId, primaryStack);
        await InsertFixtureItemAsync(
            connection,
            transaction,
            characterId,
            slot: 0,
            equipment.Value);
        await InsertFixtureItemAsync(
            connection,
            transaction,
            characterId,
            slot: 1,
            primary);
        var oddsSelections =
            new List<EquipmentForgeCommandSelection>(odds.Count);
        foreach (var source in odds)
        {
            var item = Item(source.ItemId, source.Stack);
            await InsertFixtureItemAsync(
                connection,
                transaction,
                characterId,
                source.Slot,
                item);
            oddsSelections.Add(Selection(
                EquipmentForgeCommandItemRole.OddsMaterial,
                source.Slot,
                source.Quantity,
                item));
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "equipment-forge fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new ForgeFixture(
            username,
            accountId,
            characterId,
            new CommandSubject(accountId, characterId),
            Selection(
                EquipmentForgeCommandItemRole.Equipment,
                0,
                1,
                equipment.Value),
            Selection(
                EquipmentForgeCommandItemRole.PrimaryMaterial,
                1,
                1,
                primary),
            oddsSelections);
    }

    private static async Task InsertFixtureItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short slot,
        CompactItemEntry item)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                attribute1,
                attribute_level1,
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
                @attribute1,
                @attributeLevel1,
                @quality,
                @grade,
                @bound,
                @stack,
                @itemExp,
                @holySuitCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)item.Id));
        command.Parameters.Add(
            "attribute1",
            NpgsqlDbType.Smallint).Value =
            item.Attribute1.HasValue
                ? checked((short)item.Attribute1.Value)
                : DBNull.Value;
        command.Parameters.Add(
            "attributeLevel1",
            NpgsqlDbType.Smallint).Value =
            item.AttributeLevel1.HasValue
                ? item.AttributeLevel1.Value
                : DBNull.Value;
        command.Parameters.AddWithValue("quality", item.Quality);
        command.Parameters.AddWithValue("grade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        command.Parameters.AddWithValue("itemExp", item.Exp);
        command.Parameters.AddWithValue(
            "holySuitCode",
            item.HolySuitCode);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"forge fixture item {item.Id} inserted");
    }

    private static async Task<ForgeDurableState> ReadStateAsync(
        string connectionString,
        ForgeFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row."Money",
                character_row.wallet_revision,
                character_row.inventory_revision,
                (SELECT count(*) FROM public.command_audit
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.character_currency_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = @reasonCode),
                (SELECT count(*) FROM public.character_inventory_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = @reasonCode),
                (SELECT count(*) FROM public.outbox_events
                 WHERE aggregate_key = @aggregateKey
                   AND event_type = @eventType),
                COALESCE((SELECT max(duplicate_count)
                 FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0)::integer,
                COALESCE((SELECT max(request_conflict_count)
                 FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0)::integer,
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily
                   AND result_code = 'terminal_rejected')
            FROM public.character_base character_row
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
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
        command.Parameters.AddWithValue(
            "reasonCode",
            EquipmentForgePersistenceCodec.LedgerReasonCode);
        command.Parameters.AddWithValue(
            "eventType",
            EquipmentForgePersistenceCodec.EventType);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The equipment-forge fixture disappeared.");
        }

        return new ForgeDurableState(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt64(10));
    }

    private static async Task<ForgeSlotState?> ReadSlotAsync(
        string connectionString,
        int characterId,
        short slot)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT prop_id, item_quality, item_grade, stack
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @slot;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new ForgeSlotState(
                checked((uint)reader.GetInt32(0)),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetInt16(3))
            : null;
    }

    private static async Task AssertReconciledAsync(
        string connectionString,
        ForgeFixture fixture,
        string description)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT is_reconciled
                 FROM public.character_wallet_reconciliation
                 WHERE character_id = @characterId),
                (SELECT is_reconciled
                 FROM public.character_inventory_reconciliation
                 WHERE character_id = @characterId);
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetBoolean(0) &&
            reader.GetBoolean(1),
            description);
    }

    private static EquipmentForgeCommandSelection Selection(
        EquipmentForgeCommandItemRole role,
        int slot,
        int quantity,
        CompactItemEntry item) =>
        new(role, slot, quantity, item.ToCompactString());

    private static CompactItemEntry Item(
        uint id,
        short stack) =>
        CompactItemEntry.Parse(
            $"[{id},,,,,,1,1,0,{stack},0,0,,,,,,0,,,,,,,,,,,,]");

    private sealed record ForgeFixture(
        string Username,
        int AccountId,
        int CharacterId,
        CommandSubject Subject,
        EquipmentForgeCommandSelection Equipment,
        EquipmentForgeCommandSelection Primary,
        IReadOnlyList<EquipmentForgeCommandSelection> Odds);

    private sealed record ForgeDurableState(
        int Silver,
        long WalletRevision,
        long InventoryRevision,
        long AuditCount,
        long InboxCount,
        long CurrencyLedgerCount,
        long InventoryLedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        long TerminalRejectedCount);

    private sealed record ForgeSlotState(
        uint ItemId,
        short Quality,
        short Grade,
        short Stack);
}
