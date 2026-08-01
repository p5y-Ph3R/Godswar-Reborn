using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<GameCharacter?> MoveEquipmentToKitBagAsync(
        int accountId,
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long? itemRowId = null;
        byte profession = 0;
        string equipment = string.Empty;
        await using (var command = new NpgsqlCommand($"""
            SELECT ci.id, cb.profession,
                   COALESCE(equipment_projection.equip, '')
            FROM character_base cb
            JOIN character_items ci ON ci.user_id = cb.id
            {PostgresCharacterItemProjectionSql.EquipmentJoinForCharacterAlias}
            WHERE cb.account_id = @accountId
              AND cb.id = @characterId
              AND ci.item_location = @equipmentLocation
              AND ci.slot_index = @equipmentSlot
            FOR UPDATE OF cb, ci;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("equipmentLocation", ItemLocationEquipment);
            command.Parameters.AddWithValue("equipmentSlot", (short)equipmentSlot);
            AddItemContentRevisionParameter(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                itemRowId = reader.GetInt64(0);
                profession = (byte)reader.GetInt16(1);
                equipment = reader.GetString(2);
            }
        }

        if (itemRowId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await GetCharacterByIdAsync(characterId, cancellationToken);
        }

        var unequipEligibility = EquipmentEligibility.ValidateUnequip(
            profession,
            equipment,
            equipmentSlot);
        if (!unequipEligibility.Allowed)
        {
            await transaction.CommitAsync(cancellationToken);
            return await GetCharacterByIdAsync(characterId, cancellationToken);
        }

        var destinationSlot = await ResolveRequestedEmptyKitBagSlotAsync(
            connection,
            transaction,
            characterId,
            kitBagSlot,
            cancellationToken);
        if (destinationSlot is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await GetCharacterByIdAsync(characterId, cancellationToken);
        }

        await UpdateCharacterItemSlotAsync(
            connection,
            transaction,
            itemRowId.Value,
            ItemLocationKitBag,
            destinationSlot.Value,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return await GetCharacterByIdAsync(characterId, cancellationToken);
    }

    public async Task<GameCharacter?> MoveKitBagToEquipmentAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        int requestedEquipmentSlot,
        CancellationToken cancellationToken = default,
        bool requireEmptyEquipmentSlot = false)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string equipment;
        byte profession;
        int characterLevel;
        await using (var command = new NpgsqlCommand($"""
            SELECT cb.profession, cb.fighter_job_lv,
                   COALESCE(equipment_projection.equip, '')
            FROM character_base cb
            {PostgresCharacterItemProjectionSql.EquipmentJoinForCharacterAlias}
            WHERE cb.account_id = @accountId AND cb.id = @characterId
            FOR UPDATE OF cb;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            AddItemContentRevisionParameter(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            profession = (byte)reader.GetInt16(0);
            characterLevel = reader.GetInt32(1);
            equipment = reader.GetString(2);
        }

        long? kitBagRowId = null;
        uint itemId;
        await using (var command = new NpgsqlCommand("""
            SELECT id, prop_id
            FROM character_items
            WHERE user_id = @characterId
              AND item_location = @kitBagLocation
              AND slot_index = @kitBagSlot
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
            command.Parameters.AddWithValue("kitBagSlot", (short)kitBagSlot);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                kitBagRowId = reader.GetInt64(0);
                itemId = (uint)reader.GetInt32(1);
            }
            else
            {
                itemId = 0;
            }
        }

        if (kitBagRowId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (itemId == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (!EquipmentSlots.TryGetAuthoritativeSlot(
                ItemContent.Templates,
                itemId,
                out var defaultEquipmentSlot))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var equipmentSlot = EquipmentSlots.ResolveSlotForItem(
            ItemContent.Templates,
            itemId,
            requestedEquipmentSlot,
            equipment,
            profession,
            defaultEquipmentSlot);
        if (!EquipmentSlots.IsEquipmentSlot(equipmentSlot))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var equipEligibility = EquipmentEligibility.ValidateEquip(
            ItemContent,
            profession,
            characterLevel,
            equipment,
            itemId,
            equipmentSlot);
        if (!equipEligibility.Allowed)
        {
            await transaction.CommitAsync(cancellationToken);
            return await GetCharacterByIdAsync(characterId, cancellationToken);
        }

        long? previousEquipmentRowId = null;
        await using (var command = new NpgsqlCommand("""
            SELECT id
            FROM character_items
            WHERE user_id = @characterId
              AND item_location = @equipmentLocation
              AND slot_index = @equipmentSlot
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("equipmentLocation", ItemLocationEquipment);
            command.Parameters.AddWithValue("equipmentSlot", (short)equipmentSlot);

            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is not null && scalar is not DBNull)
            {
                previousEquipmentRowId = Convert.ToInt64(scalar);
            }
        }

        if (requireEmptyEquipmentSlot && previousEquipmentRowId is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await GetCharacterByIdAsync(characterId, cancellationToken);
        }

        if (previousEquipmentRowId is null)
        {
            await UpdateCharacterItemSlotAsync(
                connection,
                transaction,
                kitBagRowId.Value,
                ItemLocationEquipment,
                equipmentSlot,
                cancellationToken);
        }
        else
        {
            var tempSlot = await AllocateTempItemSlotAsync(connection, transaction, characterId, cancellationToken);
            await UpdateCharacterItemSlotAsync(
                connection,
                transaction,
                kitBagRowId.Value,
                itemLocation: 2,
                tempSlot,
                cancellationToken);
            await UpdateCharacterItemSlotAsync(
                connection,
                transaction,
                previousEquipmentRowId.Value,
                ItemLocationKitBag,
                kitBagSlot,
                cancellationToken);
            await UpdateCharacterItemSlotAsync(
                connection,
                transaction,
                kitBagRowId.Value,
                ItemLocationEquipment,
                equipmentSlot,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetCharacterByIdAsync(characterId, cancellationToken);
    }

    public async Task<GameCharacter?> MoveKitBagItemAsync(
        int accountId,
        int characterId,
        int sourceSlot,
        int destinationSlot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT true
            FROM character_base cb
            WHERE cb.account_id = @accountId AND cb.id = @characterId
            FOR UPDATE OF cb;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);

            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null)
            {
                return null;
            }
        }

        if (sourceSlot != destinationSlot)
        {
            long sourceRowId;
            await using (var command = new NpgsqlCommand("""
                SELECT id
                FROM character_items
                WHERE user_id = @characterId
                  AND item_location = @kitBagLocation
                  AND slot_index = @sourceSlot
                FOR UPDATE;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("characterId", characterId);
                command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
                command.Parameters.AddWithValue("sourceSlot", (short)sourceSlot);

                var scalar = await command.ExecuteScalarAsync(cancellationToken);
                if (scalar is null || scalar is DBNull)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return await GetCharacterByIdAsync(characterId, cancellationToken);
                }

                sourceRowId = Convert.ToInt64(scalar);
            }

            long? destinationRowId = null;
            await using (var command = new NpgsqlCommand("""
                SELECT id
                FROM character_items
                WHERE user_id = @characterId
                  AND item_location = @kitBagLocation
                  AND slot_index = @destinationSlot
                FOR UPDATE;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("characterId", characterId);
                command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
                command.Parameters.AddWithValue("destinationSlot", (short)destinationSlot);

                var scalar = await command.ExecuteScalarAsync(cancellationToken);
                if (scalar is not null && scalar is not DBNull)
                {
                    destinationRowId = Convert.ToInt64(scalar);
                }
            }

            if (destinationRowId is null)
            {
                await UpdateCharacterItemSlotAsync(
                    connection,
                    transaction,
                    sourceRowId,
                    ItemLocationKitBag,
                    destinationSlot,
                    cancellationToken);
            }
            else
            {
                var tempSlot = await AllocateTempItemSlotAsync(connection, transaction, characterId, cancellationToken);
                await UpdateCharacterItemSlotAsync(
                    connection,
                    transaction,
                    sourceRowId,
                    itemLocation: 2,
                    tempSlot,
                    cancellationToken);
                await UpdateCharacterItemSlotAsync(
                    connection,
                    transaction,
                    destinationRowId.Value,
                    ItemLocationKitBag,
                    sourceSlot,
                    cancellationToken);
                await UpdateCharacterItemSlotAsync(
                    connection,
                    transaction,
                    sourceRowId,
                    ItemLocationKitBag,
                    destinationSlot,
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetCharacterByIdAsync(characterId, cancellationToken);
    }

    public async Task<GameCharacter?> DeleteKitBagItemAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT true
            FROM character_base cb
            WHERE cb.account_id = @accountId AND cb.id = @characterId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);

            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null)
            {
                return null;
            }
        }

        await DeleteCharacterItemSlotAsync(
            connection,
            transaction,
            characterId,
            ItemLocationKitBag,
            kitBagSlot,
            "client-ground-delete",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetCharacterByIdAsync(characterId, cancellationToken);
    }

    public async Task<GameCharacter?> ClearKitBagAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT true
            FROM character_base
            WHERE account_id = @accountId AND id = @characterId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is null)
            {
                return null;
            }
        }

        // Delete and audit only the authoritative kit-bag location. Equipment,
        // warehouse/storage rows, currency, and all other character state are
        // outside this predicate.
        await using (var command = new NpgsqlCommand("""
            WITH deleted AS (
                DELETE FROM character_items
                WHERE user_id = @characterId
                  AND item_location = @kitBagLocation
                RETURNING *
            )
            INSERT INTO character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, item_quality, item_grade, item_exp, old_item
            )
            SELECT
                'developer-clearbag',
                'delete',
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                item_exp,
                to_jsonb(deleted)
            FROM deleted;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("kitBagLocation", ItemLocationKitBag);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetCharacterByIdAsync(characterId, cancellationToken);
    }

}
