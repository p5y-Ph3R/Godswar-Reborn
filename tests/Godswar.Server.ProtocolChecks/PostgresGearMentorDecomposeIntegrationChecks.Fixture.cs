using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearMentorDecomposeIntegrationChecks
{
    private static async Task<DecomposeFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        IReadOnlyList<GearSpec> gears,
        int playerLevel = 80,
        IReadOnlyList<GearSpec>? otherItems = null)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario =
            scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09dc_{shortScenario}_{token}";
        var characterName = $"DC{shortScenario}{token}";
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
                @playerLevel,
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
            character.Parameters.AddWithValue(
                "playerLevel",
                playerLevel);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The fixture character insert returned no identity."));
        }

        foreach (var item in gears.Concat(otherItems ?? []))
        {
            await InsertFixtureItemAsync(
                connection,
                transaction,
                characterId,
                item);
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "Decompose fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();

        return new DecomposeFixture(
            accountId,
            characterId,
            gears
                .Select(static gear =>
                    new GearMentorDecomposeSelection(
                        gear.Slot,
                        gear.ToCompactItem().ToCompactString()))
                .ToArray());
    }

    private static async Task InsertFixtureItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        GearSpec item)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                attribute1,
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
                @quality,
                @grade,
                @bound,
                @stack,
                0,
                0
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", item.Slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)item.ItemId));
        command.Parameters.AddWithValue(
            "attribute1",
            item.Attribute1.HasValue
                ? item.Attribute1.Value
                : DBNull.Value);
        command.Parameters.AddWithValue("quality", item.Quality);
        command.Parameters.AddWithValue("grade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"Decompose fixture item {item.ItemId} inserted");
    }

    private static async Task<DecomposeDurableState> ReadStateAsync(
        string connectionString,
        DecomposeFixture fixture)
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
                      AND inbox.result_code = 'terminal_rejected'
                )
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
                "The Decompose fixture disappeared.");
        }

        return new DecomposeDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt64(7));
    }

    private static async Task<IReadOnlyList<StoredItem>> ReadItemsAsync(
        string connectionString,
        int characterId)
    {
        var items = new List<StoredItem>();
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
            ORDER BY slot_index;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(
                new StoredItem(
                    reader.GetInt16(0),
                    checked((uint)reader.GetInt32(1)),
                    reader.GetInt16(2),
                    reader.GetInt16(3),
                    reader.GetInt16(4),
                    reader.GetInt16(5)));
        }

        return items;
    }

    private static void AddStateParameters(
        NpgsqlCommand command,
        DecomposeFixture fixture)
    {
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalType",
            GearMentorDecomposePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            GearMentorDecomposePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            GearMentorDecomposePersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            GearMentorDecomposePersistenceCodec.CommandFamilyCode);
        command.Parameters.AddWithValue(
            "ledgerReason",
            GearMentorDecomposePersistenceCodec.LedgerReasonCode);
        command.Parameters.AddWithValue(
            "eventType",
            GearMentorDecomposePersistenceCodec.EventType);
    }

    private sealed record DecomposeFixture(
        int AccountId,
        int CharacterId,
        IReadOnlyList<GearMentorDecomposeSelection> Selections)
    {
        public CommandSubject Subject =>
            new(AccountId, CharacterId);
    }

    private readonly record struct GearSpec(
        short Slot,
        uint ItemId,
        short Quality = 2,
        short Grade = 1,
        short Bound = 1,
        short? Attribute1 = 0,
        short Stack = 1)
    {
        public CompactItemEntry ToCompactItem()
        {
            var attribute = Attribute1.HasValue
                ? Attribute1.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            return CompactItemEntry.Parse(
                $"[{ItemId},{attribute},,,,," +
                $"{Quality},{Grade},{Bound},{Stack},0]");
        }
    }

    private sealed record DecomposeDurableState(
        long InventoryRevision,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        long RejectedInboxCount);

    private readonly record struct StoredItem(
        short Slot,
        uint ItemId,
        short Quality,
        short Grade,
        short Bound,
        short Stack);
}
