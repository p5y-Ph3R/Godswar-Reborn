using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearEnhancementIntegrationChecks
{
    private static async Task<EnhancementFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        GearEnhancementCommandOperation operation,
        ItemSpec? gear = null,
        ItemSpec? catalyst = null,
        ItemSpec? stone = null,
        int? npcId = null,
        int? dialogIndex = null)
    {
        gear ??= operation == GearEnhancementCommandOperation.Add
            ? ItemSpec.Create(slot: 4, itemId: 1000)
            : ItemSpec.Create(
                slot: 4,
                itemId: 1000,
                attribute1: 0,
                attributeLevel1: 1);
        catalyst ??= operation switch
        {
            GearEnhancementCommandOperation.Add =>
                ItemSpec.Create(5, 9990, stack: 2),
            GearEnhancementCommandOperation.Enhance =>
                ItemSpec.Create(5, 9960, stack: 2),
            GearEnhancementCommandOperation.Delete =>
                ItemSpec.Create(5, 9991, stack: 2),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        stone ??= ItemSpec.Create(6, 9930, stack: 2);

        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario =
            scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09ge_{shortScenario}_{token}";
        var characterName = $"GE{shortScenario}{token}";
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

        foreach (var item in new[] { gear.Value, catalyst.Value, stone.Value })
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
            "Gear Enhancement fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();

        var endpointNpc = npcId ??
            GearEnhancementCommandEnvelope.SpartaGearMentorNpcId;
        var endpointDialog = dialogIndex ??
            GearEnhancementCommandEnvelope.GearMentorDialogIndex;
        return new EnhancementFixture(
            accountId,
            characterId,
            operation,
            endpointNpc,
            endpointDialog,
            gear.Value.ToSelection(
                GearEnhancementCommandItemRole.Gear),
            catalyst.Value.ToSelection(
                GearEnhancementCommandItemRole.Catalyst),
            stone.Value.ToSelection(
                GearEnhancementCommandItemRole.AttributeStone));
    }

    private static async Task InsertFixtureItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        ItemSpec item)
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
        command.Parameters.AddWithValue("slot", item.Slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)item.Item.Id));
        command.Parameters.AddWithValue(
            "attribute1",
            item.Item.Attribute1.HasValue
                ? item.Item.Attribute1.Value
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "attributeLevel1",
            item.Item.AttributeLevel1.HasValue
                ? item.Item.AttributeLevel1.Value
                : DBNull.Value);
        command.Parameters.AddWithValue("bound", item.Item.Bound);
        command.Parameters.AddWithValue("stack", item.Item.Stack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"Gear Enhancement fixture item {item.Item.Id} inserted");
    }

    private static async Task<EnhancementDurableState> ReadStateAsync(
        string connectionString,
        EnhancementFixture fixture)
    {
        var family = GearEnhancementCommandEnvelope.Family(
            fixture.Operation);
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
                (SELECT count(*) FROM public.command_audit
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.character_inventory_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = @reasonCode),
                (SELECT count(*) FROM public.outbox_events
                 WHERE aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND event_type = @eventType),
                COALESCE((SELECT max(duplicate_count)
                 FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0)::integer,
                COALESCE((SELECT max(request_conflict_count)
                 FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0)::integer,
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily
                   AND result_code = 'terminal_rejected')
            FROM public.character_base character_row
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalType",
            GearEnhancementPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            GearEnhancementPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            GearEnhancementPersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            GearEnhancementPersistenceCodec.CommandFamilyCode(family));
        command.Parameters.AddWithValue(
            "reasonCode",
            GearEnhancementPersistenceCodec.LedgerReasonCode(family));
        command.Parameters.AddWithValue(
            "eventType",
            GearEnhancementPersistenceCodec.EventType(family));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Gear Enhancement fixture disappeared.");
        }

        return new EnhancementDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt64(7));
    }

    private static async Task<StoredItem?> ReadItemAsync(
        string connectionString,
        int characterId,
        short slot)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                prop_id,
                attribute1,
                attribute_level1,
                bound,
                stack
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
            ? new StoredItem(
                checked((uint)reader.GetInt32(0)),
                reader.IsDBNull(1) ? null : reader.GetInt16(1),
                reader.IsDBNull(2) ? null : reader.GetInt16(2),
                reader.GetInt16(3),
                reader.GetInt16(4))
            : null;
    }

    private static async Task<IReadOnlyList<StoredLedgerEntry>>
        ReadLedgerAsync(
            string connectionString,
            EnhancementFixture fixture)
    {
        var entries = new List<StoredLedgerEntry>();
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                entry_ordinal,
                (before_state ->> 'prop_id')::integer,
                mutation_kind,
                reason_code
            FROM public.character_inventory_ledger
            WHERE account_id = @accountId
              AND character_id = @characterId
            ORDER BY entry_ordinal;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(
                new StoredLedgerEntry(
                    reader.GetInt16(0),
                    checked((uint)reader.GetInt32(1)),
                    reader.GetString(2),
                    reader.GetString(3)));
        }

        return entries;
    }

    private sealed record EnhancementFixture(
        int AccountId,
        int CharacterId,
        GearEnhancementCommandOperation Operation,
        int NpcId,
        int DialogIndex,
        GearEnhancementCommandSelection Gear,
        GearEnhancementCommandSelection Catalyst,
        GearEnhancementCommandSelection Stone)
    {
        public CommandSubject Subject =>
            new(AccountId, CharacterId);
    }

    private readonly record struct ItemSpec(
        short Slot,
        CompactItemEntry Item)
    {
        public static ItemSpec Create(
            short slot,
            uint itemId,
            short stack = 1,
            short bound = 0,
            short? attribute1 = null,
            short? attributeLevel1 = null)
        {
            var attribute = attribute1?.ToString(
                CultureInfo.InvariantCulture) ?? string.Empty;
            var level = attributeLevel1?.ToString(
                CultureInfo.InvariantCulture) ?? string.Empty;
            return new ItemSpec(
                slot,
                CompactItemEntry.Parse(
                    $"[{itemId},{attribute},,,,,1,1,{bound},{stack},0,0," +
                    $"{level},,,,,0,,,,,,,,,,,,]"));
        }

        public GearEnhancementCommandSelection ToSelection(
            GearEnhancementCommandItemRole role) =>
            new(role, Slot, Item.ToCompactString());
    }

    private sealed record EnhancementDurableState(
        long InventoryRevision,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        long RejectedInboxCount);

    private readonly record struct StoredItem(
        uint ItemId,
        short? Attribute1,
        short? AttributeLevel1,
        short Bound,
        short Stack);

    private readonly record struct StoredLedgerEntry(
        short Ordinal,
        uint ItemId,
        string MutationKind,
        string ReasonCode);
}
