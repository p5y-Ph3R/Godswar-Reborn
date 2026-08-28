using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                fighter_job_lv,
                fighter_job_exp,
                progression_reward_revision,
                inventory_revision,
                server_id
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
              AND server_id = @realmId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        command.Parameters.AddWithValue(
            "realmId",
            _realmCalendar.RealmId.Value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var value = new LockedCharacter(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            new RealmId(reader.GetInt32(4)));
        return value.Level > 0 &&
            value.Experience is >= 0 and <= uint.MaxValue &&
            value.ProgressionRevision >= 0 &&
            value.InventoryRevision >= 0 &&
            value.RealmId.IsValid
            ? value
            : throw new InvalidDataException(
                "The locked Holy Suit character state is invalid.");
    }

    private async Task<LockedKitBag> LockKitBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        var occupied = new bool[96];
        var items = new Dictionary<short, LockedInventoryItem>();
        await using var command = CreateCommand(
            """
            SELECT
                id, slot_index, prop_id,
                attribute1, attribute2, attribute3, attribute4,
                attribute5,
                attribute_level1, attribute_level2,
                attribute_level3, attribute_level4,
                attribute_level5,
                item_quality, item_grade, bound, stack, item_exp,
                holy_suit_code, holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level,
                holy_socket6_effect_id, holy_socket6_level,
                to_jsonb(character_items)::text,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2,
                holy_socket1_value, holy_socket2_value,
                holy_socket3_value, holy_socket4_value
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index BETWEEN 0 AND 95
            ORDER BY slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(1);
            var item = ReadCompactItem(reader);
            if (item.IsEmpty || item.Stack <= 0 || occupied[slot] ||
                !items.TryAdd(slot, new LockedInventoryItem(
                    reader.GetInt64(0),
                    slot,
                    item,
                    reader.GetString(32))))
            {
                throw new InvalidDataException(
                    "The authoritative Holy Suit kit bag is invalid.");
            }
            occupied[slot] = true;
        }

        var empty = Enumerable.Range(0, occupied.Length)
            .Where(slot => !occupied[slot])
            .Select(static slot => checked((short)slot))
            .ToArray();
        return new LockedKitBag(items, empty);
    }

    private async Task<DailyUsage> LockDailyUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId,
        DateOnly usageDay,
        CancellationToken cancellationToken)
    {
        await using (var create = CreateCommand(
            """
            INSERT INTO public.holy_suit_daily_exp_storage (
                account_id,
                realm_id,
                usage_day,
                stored_exp,
                operation_count
            )
            VALUES (
                @accountId,
                @realmId,
                @usageDay,
                0,
                0
            )
            ON CONFLICT (account_id, realm_id, usage_day)
            DO NOTHING;
            """,
            connection,
            transaction))
        {
            create.Parameters.AddWithValue("accountId", accountId);
            create.Parameters.AddWithValue("realmId", realmId.Value);
            create.Parameters.AddWithValue("usageDay", usageDay);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = CreateCommand(
            """
            SELECT usage_day, stored_exp
            FROM public.holy_suit_daily_exp_storage
            WHERE account_id = @accountId
              AND realm_id = @realmId
              AND usage_day = @usageDay
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue("usageDay", usageDay);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The Holy Suit daily usage row is unavailable.");
        }
        var usage = new DailyUsage(
            reader.GetFieldValue<DateOnly>(0),
            reader.GetInt64(1));
        return usage.UsageDay != default && usage.StoredExperience >= 0
            ? usage
            : throw new InvalidDataException(
                "The locked Holy Suit daily usage row is invalid.");
    }

    private async Task<bool> HasActiveBattlePassAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.account_entitlements
                WHERE account_id = @accountId
                  AND entitlement_key = @entitlementKey
                  AND starts_at <= now()
                  AND revoked_at IS NULL
                  AND (expires_at IS NULL OR expires_at > now())
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue(
            "entitlementKey",
            _itemContent.HolySuit.OperationPolicy!
                .DailyQuotaBypassEntitlement);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static CompactItemEntry ReadCompactItem(
        NpgsqlDataReader reader) =>
        new(
            checked((uint)reader.GetInt32(2)),
            ReadNullableInt16(reader, 3),
            ReadNullableInt16(reader, 4),
            ReadNullableInt16(reader, 5),
            ReadNullableInt16(reader, 6),
            ReadNullableInt16(reader, 7),
            reader.GetInt16(13),
            reader.GetInt16(14),
            reader.GetInt16(15),
            reader.GetInt16(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            ReadNullableInt16(reader, 8),
            ReadNullableInt16(reader, 9),
            ReadNullableInt16(reader, 10),
            ReadNullableInt16(reader, 11),
            ReadNullableInt16(reader, 12),
            reader.GetInt16(19),
            ReadNullableInt16(reader, 20),
            ReadNullableInt16(reader, 21),
            ReadNullableInt16(reader, 22),
            ReadNullableInt16(reader, 23),
            ReadNullableInt16(reader, 24),
            ReadNullableInt16(reader, 25),
            ReadNullableInt16(reader, 26),
            ReadNullableInt16(reader, 27),
            ReadNullableInt16(reader, 28),
            ReadNullableInt16(reader, 29),
            ReadNullableInt16(reader, 30),
            ReadNullableInt16(reader, 31))
        {
            ClassAttribute1 = ReadNullableInt16(reader, 33),
            ClassAttribute2 = ReadNullableInt16(reader, 34),
            ElementalAttribute1 = ReadNullableInt16(reader, 35),
            ElementalAttribute2 = ReadNullableInt16(reader, 36),
            Socket1Value = ReadNullableInt16(reader, 37),
            Socket2Value = ReadNullableInt16(reader, 38),
            Socket3Value = ReadNullableInt16(reader, 39),
            Socket4Value = ReadNullableInt16(reader, 40)
        };

    private static short? ReadNullableInt16(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);
}
