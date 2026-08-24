using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWarehouseCommandIntegrationChecks
{
    private static async Task<WarehouseFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        params ItemPlacement[] placements)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var label = scenario[..Math.Min(6, scenario.Length)];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
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
            account.Parameters.AddWithValue(
                "username",
                $"b09wh_{label}_{token}");
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "Warehouse fixture account has no identity."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession,
                fighter_job_lv, "Money", "Stone", wallet_revision,
                inventory_revision, warehouse_capacity,
                warehouse_revision)
            VALUES (
                @accountId, 1, @name, 1, 0, 80, 1000, 100,
                0, 0, 40, 0)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue(
                "name",
                $"WH{label}{token}");
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "Warehouse fixture character has no identity."));
        }

        var items = new Dictionary<(short Location, short Slot), FixtureItem>();
        foreach (var placement in placements)
        {
            var compact = Item(placement.ItemId, placement.Stack);
            await using var item = new NpgsqlCommand(
                """
                INSERT INTO public.character_items (
                    user_id, item_location, slot_index, prop_id,
                    item_quality, item_grade, bound, stack, item_exp,
                    holy_suit_code, holy_socket_count)
                VALUES (
                    @characterId, @location, @slot, @itemId,
                    1, 1, 1, @stack, 0, 0, 0)
                RETURNING id;
                """,
                connection,
                transaction);
            item.Parameters.AddWithValue("characterId", characterId);
            item.Parameters.AddWithValue("location", placement.Location);
            item.Parameters.AddWithValue("slot", placement.Slot);
            item.Parameters.AddWithValue(
                "itemId",
                checked((int)placement.ItemId));
            item.Parameters.AddWithValue("stack", placement.Stack);
            var id = Convert.ToInt64(
                await item.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "Warehouse fixture item has no identity."));
            items.Add(
                (placement.Location, placement.Slot),
                new FixtureItem(id, compact.ToCompactString()));
        }
        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                30,
                CancellationToken.None),
            "warehouse fixture captures economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new WarehouseFixture(
            accountId,
            characterId,
            new CommandSubject(accountId, characterId),
            items);
    }

    private static async Task<WarehouseDurableState> ReadStateAsync(
        string connectionString,
        WarehouseFixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT warehouse_capacity, warehouse_revision,
                   inventory_revision,
                   (SELECT count(*) FROM public.command_inbox
                    WHERE principal_key = @principalKey
                      AND command_family IN (
                        'warehouse_transfer', 'warehouse_expansion')),
                   (SELECT count(*) FROM public.command_audit
                    WHERE principal_key = @principalKey
                      AND command_family IN (
                        'warehouse_transfer', 'warehouse_expansion')),
                   (SELECT count(*) FROM public.character_inventory_ledger
                    WHERE account_id = @accountId
                      AND character_id = @characterId
                      AND reason_code IN (
                        'warehouse_transfer', 'warehouse_expansion')),
                   (SELECT count(*) FROM public.outbox_events event
                    JOIN public.command_inbox inbox
                      ON inbox.id = event.command_inbox_id
                    WHERE inbox.principal_key = @principalKey
                      AND inbox.command_family IN (
                        'warehouse_transfer', 'warehouse_expansion')),
                   (SELECT count(*) FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 2),
                   (SELECT count(*)
                    FROM public.warehouse_expansion_settlements
                    WHERE account_id = @accountId
                      AND character_id = @characterId)
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException("Warehouse fixture disappeared.");
        }
        return new WarehouseDurableState(
            reader.GetInt16(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
    }

    private static async Task<(long Id, short Stack)?> ReadItemAsync(
        string connectionString,
        WarehouseFixture fixture,
        short location,
        short slot)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id, stack
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = @location
              AND slot_index = @slot;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue("location", location);
        command.Parameters.AddWithValue("slot", slot);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetInt64(0), reader.GetInt16(1))
            : null;
    }

    private static CompactItemEntry Item(uint id, short stack) =>
        CompactItemEntry.Parse(
            $"[{id},,,,,,1,1,1,{stack},0,0,,,,,,0,,,,,,,,,,,,]");

    private readonly record struct ItemPlacement(
        short Location,
        short Slot,
        short Stack,
        uint ItemId = 4102);

    private sealed record FixtureItem(long Id, string CompactState);

    private sealed record WarehouseFixture(
        int AccountId,
        int CharacterId,
        CommandSubject Subject,
        IReadOnlyDictionary<(short Location, short Slot), FixtureItem> Items);

    private sealed record WarehouseDurableState(
        int Capacity,
        long WarehouseRevision,
        long InventoryRevision,
        long InboxCount,
        long AuditCount,
        long LedgerCount,
        long OutboxCount,
        long TemporaryCount,
        long SettlementCount);
}
