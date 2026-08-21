using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresClassSuitCommandIntegrationChecks
{
    private static async Task<ClassSuitFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        short insigniaStack = 5)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"b09cs_{scenario}_{token}";
        var gear = CreateCommonGear();
        var insignia = CreateInsignia(insigniaStack);

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        int accountId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await command.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Class Suit account insert returned no identity."));
        }

        int characterId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession, fighter_job_lv,
                "Money", "Stone", inventory_revision
            )
            VALUES (@accountId, 1, @name, 1, 0, 120, 1000, 100, 0)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue(
                "name",
                $"CS{scenario}{token}");
            characterId = Convert.ToInt32(
                await command.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Class Suit character insert returned no identity."));
        }

        await InsertItemAsync(
            connection,
            transaction,
            characterId,
            GearSlot,
            gear);
        await InsertItemAsync(
            connection,
            transaction,
            characterId,
            InsigniaSlot,
            insignia);
        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "Class Suit fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();

        return new ClassSuitFixture(
            accountId,
            characterId,
            gear.ToCompactString(),
            insignia.ToCompactString(),
            insigniaStack);
    }

    private static async Task InsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short slot,
        CompactItemEntry item)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                attribute1, attribute2,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2,
                attribute_level1, attribute_level2,
                item_quality, item_grade, bound, stack, item_exp,
                holy_suit_code, holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level
            )
            VALUES (
                @characterId, 1, @slot, @itemId,
                @attribute1, @attribute2,
                @classAttribute1, @classAttribute2,
                @elementalAttribute1, @elementalAttribute2,
                @attributeLevel1, @attributeLevel2,
                @quality, @grade, @bound, @stack, @itemExp,
                @holySuitCode, @socketCount,
                @socket1Effect, @socket1Level,
                @socket2Effect, @socket2Level
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("itemId", checked((int)item.Id));
        AddNullableSmallint(command, "attribute1", item.Attribute1);
        AddNullableSmallint(command, "attribute2", item.Attribute2);
        AddNullableSmallint(
            command,
            "classAttribute1",
            item.ClassAttribute1);
        AddNullableSmallint(
            command,
            "classAttribute2",
            item.ClassAttribute2);
        AddNullableSmallint(
            command,
            "elementalAttribute1",
            item.ElementalAttribute1);
        AddNullableSmallint(
            command,
            "elementalAttribute2",
            item.ElementalAttribute2);
        AddNullableSmallint(
            command,
            "attributeLevel1",
            item.AttributeLevel1);
        AddNullableSmallint(
            command,
            "attributeLevel2",
            item.AttributeLevel2);
        command.Parameters.AddWithValue("quality", item.Quality);
        command.Parameters.AddWithValue("grade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        command.Parameters.AddWithValue("itemExp", item.Exp);
        command.Parameters.AddWithValue("holySuitCode", item.HolySuitCode);
        command.Parameters.AddWithValue("socketCount", item.SocketCount);
        AddNullableSmallint(
            command,
            "socket1Effect",
            item.Socket1EffectId);
        AddNullableSmallint(
            command,
            "socket1Level",
            item.Socket1Level);
        AddNullableSmallint(
            command,
            "socket2Effect",
            item.Socket2EffectId);
        AddNullableSmallint(
            command,
            "socket2Level",
            item.Socket2Level);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"Class Suit fixture item {item.Id} inserted");
    }

    private static void AddNullableSmallint(
        NpgsqlCommand command,
        string name,
        int? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Smallint).Value =
            value.HasValue ? checked((short)value.Value) : DBNull.Value;

    private static CompactItemEntry CreateCommonGear() =>
        CompactItemEntry.Empty with
        {
            Id = 1013,
            Attribute1 = 40,
            AttributeLevel1 = 3,
            Quality = 20,
            Grade = 25,
            Bound = 0,
            Stack = 1,
            Exp = 777,
            HolySuitCode = 705,
            SocketCount = 2,
            Socket1EffectId = 501,
            Socket1Level = 2,
            Socket2EffectId = 502,
            Socket2Level = 3
        };

    private static CompactItemEntry CreateInsignia(short stack) =>
        CompactItemEntry.Empty with
        {
            Id = ClassSuitConversionCatalog.PromotionalInsigniaI,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = stack
        };

    private static async Task<ClassSuitDurableState> ReadStateAsync(
        string connectionString,
        ClassSuitFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var family = ClassSuitPersistenceCodec.FamilyCode(
            CommandFamily.ClassSuitExchangeTierI);
        var aggregateKey = ClassSuitPersistenceCodec.AggregateKey(
            fixture.CharacterId);
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
                   AND reason_code = @commandFamily),
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
                   AND command_family = @commandFamily), 0),
                COALESCE((SELECT max(request_conflict_count)
                 FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0),
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily
                   AND result_code = 'committed'),
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily
                   AND result_code = 'terminal_rejected')
            FROM public.character_base character_row
            WHERE character_row.id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalType",
            ClassSuitPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            ClassSuitPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue("commandFamily", family);
        command.Parameters.AddWithValue(
            "eventType",
            ClassSuitPersistenceCodec.EventType);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Class Suit fixture character disappeared.");
        }

        var counters = new long[9];
        for (var index = 0; index < counters.Length; index++)
        {
            counters[index] = Convert.ToInt64(reader.GetValue(index));
        }
        await reader.CloseAsync();

        return new ClassSuitDurableState(
            counters[0],
            await ReadItemAsync(connection, fixture.CharacterId, GearSlot),
            await ReadItemAsync(
                connection,
                fixture.CharacterId,
                InsigniaSlot),
            counters[1],
            counters[2],
            counters[3],
            counters[4],
            checked((int)counters[5]),
            checked((int)counters[6]),
            counters[7],
            counters[8]);
    }

    private static async Task<CompactItemEntry> ReadItemAsync(
        NpgsqlConnection connection,
        int characterId,
        short slot)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                prop_id, attribute1, attribute2, attribute3,
                attribute4, attribute5, item_quality, item_grade,
                bound, stack, item_exp, holy_suit_code,
                attribute_level1, attribute_level2, attribute_level3,
                attribute_level4, attribute_level5, holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level,
                holy_socket6_effect_id, holy_socket6_level,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @slot;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return CompactItemEntry.Empty;
        }

        return new CompactItemEntry(
            checked((uint)reader.GetInt32(0)),
            NullableInt16(reader, 1),
            NullableInt16(reader, 2),
            NullableInt16(reader, 3),
            NullableInt16(reader, 4),
            NullableInt16(reader, 5),
            reader.GetInt16(6),
            reader.GetInt16(7),
            reader.GetInt16(8),
            reader.GetInt16(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            NullableInt16(reader, 12),
            NullableInt16(reader, 13),
            NullableInt16(reader, 14),
            NullableInt16(reader, 15),
            NullableInt16(reader, 16),
            reader.GetInt16(17),
            NullableInt16(reader, 18),
            NullableInt16(reader, 19),
            NullableInt16(reader, 20),
            NullableInt16(reader, 21),
            NullableInt16(reader, 22),
            NullableInt16(reader, 23),
            NullableInt16(reader, 24),
            NullableInt16(reader, 25),
            NullableInt16(reader, 26),
            NullableInt16(reader, 27),
            NullableInt16(reader, 28),
            NullableInt16(reader, 29))
        {
            ClassAttribute1 = NullableInt16(reader, 30),
            ClassAttribute2 = NullableInt16(reader, 31),
            ElementalAttribute1 = NullableInt16(reader, 32),
            ElementalAttribute2 = NullableInt16(reader, 33)
        };
    }

    private static short? NullableInt16(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);

    private sealed record ClassSuitFixture(
        int AccountId,
        int CharacterId,
        string ExpectedGearState,
        string ExpectedInsigniaState,
        short InitialInsigniaStack);

    private sealed record ClassSuitDurableState(
        long InventoryRevision,
        CompactItemEntry Gear,
        CompactItemEntry Insignia,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        int ConflictCount,
        long CommittedInboxCount,
        long RejectedInboxCount);
}
