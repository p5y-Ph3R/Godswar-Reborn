using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemDeleteCommandIntegrationChecks
{
    private const short DefaultTargetSlot = 12;

    private static async Task<DeleteFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        CompactItemEntry? item = null,
        bool targetStartsEmpty = false)
    {
        item ??= Item(4212, 2);
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09del_{shortScenario}_{token}";
        var characterName = $"KD{shortScenario}{token}";
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
                    "The delete fixture account has no identity."));
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
                1000,
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
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The delete fixture character has no identity."));
        }

        if (!targetStartsEmpty)
        {
            await InsertFixtureItemAsync(
                connection,
                transaction,
                characterId,
                DefaultTargetSlot,
                item.Value);
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "item-delete fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new DeleteFixture(
            username,
            accountId,
            characterId,
            new CommandSubject(accountId, characterId),
            DefaultTargetSlot,
            targetStartsEmpty ? "[]" : item.Value.ToCompactString());
    }

    private static async Task InsertFixtureItemAsync(
        string connectionString,
        DeleteFixture fixture,
        CompactItemEntry item)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await InsertFixtureItemAsync(
            connection,
            transaction,
            fixture.CharacterId,
            fixture.TargetSlot,
            item);
        await transaction.CommitAsync();
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
                attribute2,
                attribute3,
                attribute4,
                attribute5,
                attribute_level1,
                attribute_level2,
                attribute_level3,
                attribute_level4,
                attribute_level5,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp,
                holy_suit_code,
                holy_socket_count,
                holy_socket1_effect_id,
                holy_socket1_level,
                holy_socket2_effect_id,
                holy_socket2_level,
                holy_socket3_effect_id,
                holy_socket3_level,
                holy_socket4_effect_id,
                holy_socket4_level,
                holy_socket5_effect_id,
                holy_socket5_level,
                holy_socket6_effect_id,
                holy_socket6_level
            )
            VALUES (
                @characterId,
                1,
                @slot,
                @itemId,
                @attribute1,
                @attribute2,
                @attribute3,
                @attribute4,
                @attribute5,
                @attributeLevel1,
                @attributeLevel2,
                @attributeLevel3,
                @attributeLevel4,
                @attributeLevel5,
                @quality,
                @grade,
                @bound,
                @stack,
                @itemExp,
                @holySuitCode,
                @socketCount,
                @socket1EffectId,
                @socket1Level,
                @socket2EffectId,
                @socket2Level,
                @socket3EffectId,
                @socket3Level,
                @socket4EffectId,
                @socket4Level,
                @socket5EffectId,
                @socket5Level,
                @socket6EffectId,
                @socket6Level
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)item.Id));
        AddNullable(command, "attribute1", item.Attribute1);
        AddNullable(command, "attribute2", item.Attribute2);
        AddNullable(command, "attribute3", item.Attribute3);
        AddNullable(command, "attribute4", item.Attribute4);
        AddNullable(command, "attribute5", item.Attribute5);
        AddNullable(
            command,
            "attributeLevel1",
            item.AttributeLevel1);
        AddNullable(
            command,
            "attributeLevel2",
            item.AttributeLevel2);
        AddNullable(
            command,
            "attributeLevel3",
            item.AttributeLevel3);
        AddNullable(
            command,
            "attributeLevel4",
            item.AttributeLevel4);
        AddNullable(
            command,
            "attributeLevel5",
            item.AttributeLevel5);
        command.Parameters.AddWithValue("quality", item.Quality);
        command.Parameters.AddWithValue("grade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        command.Parameters.AddWithValue("itemExp", item.Exp);
        command.Parameters.AddWithValue(
            "holySuitCode",
            item.HolySuitCode);
        command.Parameters.AddWithValue(
            "socketCount",
            item.SocketCount);
        AddNullable(
            command,
            "socket1EffectId",
            item.Socket1EffectId);
        AddNullable(command, "socket1Level", item.Socket1Level);
        AddNullable(
            command,
            "socket2EffectId",
            item.Socket2EffectId);
        AddNullable(command, "socket2Level", item.Socket2Level);
        AddNullable(
            command,
            "socket3EffectId",
            item.Socket3EffectId);
        AddNullable(command, "socket3Level", item.Socket3Level);
        AddNullable(
            command,
            "socket4EffectId",
            item.Socket4EffectId);
        AddNullable(command, "socket4Level", item.Socket4Level);
        AddNullable(
            command,
            "socket5EffectId",
            item.Socket5EffectId);
        AddNullable(command, "socket5Level", item.Socket5Level);
        AddNullable(
            command,
            "socket6EffectId",
            item.Socket6EffectId);
        AddNullable(command, "socket6Level", item.Socket6Level);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"item-delete fixture slot {slot} inserted");
    }

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        int? value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.Smallint).Value =
            value.HasValue
                ? checked((short)value.Value)
                : DBNull.Value;

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        short? value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.Smallint).Value =
            value.HasValue ? value.Value : DBNull.Value;

    private static async Task<DeleteDurableState> ReadStateAsync(
        string connectionString,
        DeleteFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
                (SELECT count(*) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND slot_index = @slot),
                (SELECT count(*) FROM public.command_audit
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_key = @principalKey
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.character_item_audit
                 WHERE user_id = @characterId
                   AND source = 'client-ground-delete'
                   AND action = 'delete'),
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
                COALESCE((SELECT is_reconciled
                 FROM public.character_inventory_reconciliation
                 WHERE character_id = @characterId), false)
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
        command.Parameters.AddWithValue("slot", fixture.TargetSlot);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(
                CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            KitBagItemDeletePersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            KitBagItemDeletePersistenceCodec.CommandFamilyCode);
        command.Parameters.AddWithValue(
            "reasonCode",
            KitBagItemDeletePersistenceCodec.LedgerReasonCode);
        command.Parameters.AddWithValue(
            "eventType",
            KitBagItemDeletePersistenceCodec.EventType);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The item-delete fixture disappeared.");
        }

        return new DeleteDurableState(
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

    private static CompactItemEntry Item(
        uint id,
        short stack,
        short quality = 1,
        short grade = 1) =>
        CompactItemEntry.Parse(
            $"[{id},,,,,,{quality},{grade},0,{stack},0,0," +
            ",,,,,0,,,,,,,,,,,,]");

    private sealed record DeleteFixture(
        string Username,
        int AccountId,
        int CharacterId,
        CommandSubject Subject,
        short TargetSlot,
        string InitialItemState);

    private sealed record DeleteDurableState(
        long InventoryRevision,
        long TargetItemCount,
        long AuditCount,
        long InboxCount,
        long CompatibilityAuditCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        bool IsReconciled);
}
