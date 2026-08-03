using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresEquipmentBagTransferCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT inventory_revision, profession, fighter_job_lv
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
        var revision = reader.GetInt64(0);
        return revision >= 0
            ? new LockedCharacter(
                revision,
                reader.GetInt16(1),
                reader.GetInt32(2))
            : null;
    }

    private async Task<LockedTransferSlots> LockTransferSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken)
    {
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
                to_jsonb(character_items)::text,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2
            FROM public.character_items
            WHERE user_id = @characterId
              AND (
                (item_location = 0 AND slot_index = @equipmentSlot)
                OR
                (item_location = 1 AND slot_index = @kitBagSlot)
              )
            ORDER BY item_location, slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "equipmentSlot",
            checked((short)equipmentSlot));
        command.Parameters.AddWithValue(
            "kitBagSlot",
            checked((short)kitBagSlot));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        LockedItem? equipment = null;
        LockedItem? kitBag = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new LockedItem(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                ReadCompactItem(reader),
                reader.GetString(33));
            if (item.Location == 0 &&
                item.SlotIndex == equipmentSlot)
            {
                equipment = item;
            }
            else if (item.Location == 1 &&
                     item.SlotIndex == kitBagSlot)
            {
                kitBag = item;
            }
            else
            {
                throw new InvalidDataException(
                    "The locked transfer item has an unexpected " +
                    "position.");
            }
        }
        return new LockedTransferSlots(equipment, kitBag);
    }

    private async Task<EquipmentBagTransferResultStatus>
        ValidateTransferAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            TransferCommandContext context,
            LockedCharacter character,
            LockedTransferSlots slots,
            CancellationToken cancellationToken)
    {
        if (!IsSupportedEquipmentSlot(
                context.Command.EquipmentSlot))
        {
            return EquipmentBagTransferResultStatus
                .WrongEquipmentSlot;
        }

        if (slots.Equipment is not null)
        {
            if (context.Command.EquipmentSlot == EquipmentSlots.Mount &&
                await HasEquippedMountGearAsync(
                    connection,
                    transaction,
                    context.Subject.CharacterId,
                    cancellationToken))
            {
                return EquipmentBagTransferResultStatus
                    .MountDependencyBlocked;
            }
            return EquipmentBagTransferResultStatus.Unequipped;
        }

        var item = slots.KitBag ??
            throw new InvalidDataException(
                "Validated transfer has no source item.");
        var template = ReadItemTemplate(item.Item.Id);
        if (template is null ||
            !EquipmentSlots.IsEquipmentKind(template.Kind))
        {
            return EquipmentBagTransferResultStatus
                .ItemNotEquipment;
        }
        if (!TemplateMatchesSlot(
                template,
                context.Command.EquipmentSlot))
        {
            return EquipmentBagTransferResultStatus
                .WrongEquipmentSlot;
        }
        if (template.ClassIds.Length > 0 &&
            !template.ClassIds.Contains(character.Profession))
        {
            return EquipmentBagTransferResultStatus
                .ProfessionRestricted;
        }
        if (template.MinimumLevel is { } minimum &&
                character.CharacterLevel < minimum ||
            template.MaximumLevel is { } maximum &&
                character.CharacterLevel > maximum)
        {
            return EquipmentBagTransferResultStatus
                .LevelRestricted;
        }

        if (template.Kind.Equals(
                "mount",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!_itemContent.Mounts.TryGetRideDefinition(
                    item.Item.Id,
                    out _))
            {
                return EquipmentBagTransferResultStatus
                    .MountUnsupported;
            }
            if (!await MountSupportsEquippedGearAsync(
                    connection,
                    transaction,
                    context.Subject.CharacterId,
                    template.MinimumLevel ?? 1,
                    cancellationToken))
            {
                return EquipmentBagTransferResultStatus
                    .MountDependencyBlocked;
            }
        }
        else if (EquipmentEligibility.IsMountGearKind(
                     template.Kind) &&
                 !await EquippedMountSupportsGearAsync(
                     connection,
                     transaction,
                     context.Subject.CharacterId,
                     template.MinimumLevel ?? 1,
                     cancellationToken))
        {
            return EquipmentBagTransferResultStatus
                .MountDependencyBlocked;
        }

        return EquipmentBagTransferResultStatus.Equipped;
    }

    private ItemTemplate? ReadItemTemplate(uint itemId)
    {
        if (!_itemContent.Templates.TryGet(itemId, out var definition))
        {
            return null;
        }

        return new ItemTemplate(
            definition.Kind,
            definition.EquipmentSlot,
            definition.ClassIds.ToArray(),
            definition.MinLevel,
            definition.MaxLevel);
    }

    private async Task<bool> HasEquippedMountGearAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 0
              AND slot_index BETWEEN 15 AND 19
            ORDER BY slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var found = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            found = true;
        }
        return found;
    }

    private async Task<bool> EquippedMountSupportsGearAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int requiredLevel,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT ci.prop_id
            FROM public.character_items ci
            WHERE ci.user_id = @characterId
              AND ci.item_location = 0
              AND ci.slot_index = 20
            FOR UPDATE OF ci;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !_itemContent.Templates.TryGet(
                checked((uint)reader.GetInt32(0)),
                out var mountTemplate) ||
            !mountTemplate.Kind.Equals(
                "mount",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var mountLevel = mountTemplate.MinLevel ?? 1;
        return mountLevel >= requiredLevel;
    }

    private async Task<bool> MountSupportsEquippedGearAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int mountLevel,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT ci.prop_id
            FROM public.character_items ci
            WHERE ci.user_id = @characterId
              AND ci.item_location = 0
              AND ci.slot_index BETWEEN 15 AND 19
            ORDER BY ci.slot_index
            FOR UPDATE OF ci;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!_itemContent.Templates.TryGet(
                    checked((uint)reader.GetInt32(0)),
                    out var gearTemplate) ||
                !EquipmentEligibility.IsMountGearKind(
                    gearTemplate.Kind) ||
                (gearTemplate.MinLevel is { } minimumLevel &&
                 minimumLevel > mountLevel))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TemplateMatchesSlot(
        ItemTemplate template,
        int requestedSlot) =>
        template.Kind.Equals(
            "ring",
            StringComparison.OrdinalIgnoreCase)
            ? requestedSlot is EquipmentSlots.Ring1 or
                EquipmentSlots.Ring2
            : template.EquipmentSlot == requestedSlot;

    private sealed record ItemTemplate(
        string Kind,
        short EquipmentSlot,
        short[] ClassIds,
        int? MinimumLevel,
        int? MaximumLevel);
}
