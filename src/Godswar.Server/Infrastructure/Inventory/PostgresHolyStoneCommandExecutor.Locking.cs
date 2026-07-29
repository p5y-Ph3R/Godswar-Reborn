using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT "Stone", wallet_revision, inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var character = new LockedCharacter(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
        if (character.Gold < 0 ||
            character.WalletRevision < 0 ||
            character.InventoryRevision < 0)
        {
            throw new InvalidDataException(
                "The locked Holy Stone economy state is invalid.");
        }
        return character;
    }

    private async Task<LockedCommandItems> LockCommandItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandContext context,
        CancellationToken cancellationToken)
    {
        var targetLocation =
            checked((short)context.Command.TargetLocation);
        var locksWholeKitBag =
            context.Command.Operation == HolyStoneCommandOperation.Remove;
        await using var command = CreateCommand(
            """
            SELECT
                id, item_location, slot_index, prop_id,
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
                to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND (
                (
                    item_location = @targetLocation
                    AND slot_index = @targetSlot
                )
                OR (
                    @lockWholeKitBag
                    AND item_location = 1
                    AND slot_index BETWEEN 0 AND 95
                )
                OR (
                    @stoneSlot >= 0
                    AND item_location = 1
                    AND slot_index = @stoneSlot
                )
              )
            ORDER BY item_location, slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "targetLocation",
            targetLocation);
        command.Parameters.AddWithValue(
            "targetSlot",
            checked((short)context.Command.TargetSlot));
        command.Parameters.AddWithValue(
            "lockWholeKitBag",
            locksWholeKitBag);
        command.Parameters.AddWithValue(
            "stoneSlot",
            checked((short)context.Command.StoneKitBagSlot));

        LockedItem? target = null;
        LockedItem? stone = null;
        var kitBag = new Dictionary<short, LockedItem>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var locked = new LockedItem(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                ReadCompactItem(reader),
                reader.GetString(33));
            ValidatePhysicalItem(locked);
            if (locked.Location == 1 &&
                !kitBag.TryAdd(locked.Slot, locked))
            {
                throw new InvalidDataException(
                    "The locked kit bag contains a duplicate slot.");
            }
            if (locked.Location == targetLocation &&
                locked.Slot == context.Command.TargetSlot)
            {
                target = locked;
            }
            if (context.Command.Operation ==
                    HolyStoneCommandOperation.Mount &&
                locked.Location == 1 &&
                locked.Slot == context.Command.StoneKitBagSlot)
            {
                stone = locked;
            }
        }

        return new LockedCommandItems(target, stone, kitBag);
    }

    private async Task<string?> ReadItemTemplateKindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        uint itemId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT kind
            FROM public.item_templates
            WHERE id = @itemId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)itemId));
        return await command.ExecuteScalarAsync(cancellationToken)
            as string;
    }

    private static void ValidatePhysicalItem(LockedItem item)
    {
        if (item.ItemInstanceId <= 0 ||
            item.Item.IsEmpty ||
            item.Item.Stack <= 0 ||
            item.Location is < 0 or > 2 ||
            item.Slot < 0)
        {
            throw new InvalidDataException(
                "A locked physical inventory row is invalid.");
        }
    }
}
