using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private const int MysteriousTuckNetItemId = 10084;
    private const uint RockElfEggItemId = 10150;
    private const int KitBagSlotCount = 96;

    private async Task<PetTransition> CapturePetEggAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int netSlot,
        LockedBagItem net,
        PetCaptureIntent capture,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (net.PropId != MysteriousTuckNetItemId ||
            net.Stack <= 0 ||
            capture.EggItemId != RockElfEggItemId ||
            !_petContent.TryGetSpeciesByEggItemId(
                capture.EggItemId,
                out var species) ||
            species.SpeciesId != 1)
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: netSlot);
        }

        var capturedEggQuality = await RollCapturedEggQualityAsync(
            connection,
            transaction,
            species,
            capture.EggItemId,
            capture.Difficulty,
            cancellationToken);

        if (net.Stack == 1)
        {
            var eggState = await TransformNetIntoEggAsync(
                connection,
                transaction,
                characterId,
                netSlot,
                net,
                capture.EggItemId,
                capturedEggQuality,
                cancellationToken);
            var revision = await AdvanceInventoryRevisionAsync(
                connection,
                transaction,
                characterId,
                character.InventoryRevision,
                cancellationToken);
            return new(
                PetDurableReceiptStatus.PetCaptured,
                KitBagSlot: netSlot,
                InventoryMutations:
                [
                    new InventoryMutation(
                        net.ItemId,
                        "update",
                        net.BeforeState,
                        eggState,
                        "pet_capture_egg",
                        revision)
                ]);
        }

        var eggSlot = await FindEmptyKitBagSlotAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        if (eggSlot is null)
        {
            return new(
                PetDurableReceiptStatus.PetCaptureBagFull,
                KitBagSlot: netSlot);
        }

        var netState = await DecrementCaptureNetAsync(
            connection,
            transaction,
            characterId,
            netSlot,
            net,
            cancellationToken);
        var egg = await InsertCapturedEggAsync(
            connection,
            transaction,
            characterId,
            eggSlot.Value,
            capture.EggItemId,
            capturedEggQuality,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            characterId,
            character.InventoryRevision,
            cancellationToken);
        return new(
            PetDurableReceiptStatus.PetCaptured,
            KitBagSlot: netSlot,
            InventoryMutations:
            [
                new InventoryMutation(
                    net.ItemId,
                    "update",
                    net.BeforeState,
                    netState,
                    "pet_capture_net_consumed",
                    inventoryRevision),
                new InventoryMutation(
                    egg.ItemId,
                    "add",
                    null,
                    egg.State,
                    "pet_capture_egg",
                    inventoryRevision)
            ]);
    }

    private async Task<int?> FindEmptyKitBagSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT candidate.slot_index
            FROM generate_series(0, @lastSlot) AS candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1
                FROM public.character_items item
                WHERE item.user_id = @characterId
                  AND item.item_location = 1
                  AND item.slot_index = candidate.slot_index
            )
            ORDER BY candidate.slot_index
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("lastSlot", KitBagSlotCount - 1);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private async Task<string> TransformNetIntoEggAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int netSlot,
        LockedBagItem net,
        uint eggItemId,
        short capturedEggQuality,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET prop_id = @eggItemId,
                attribute1 = NULL,
                attribute2 = NULL,
                attribute3 = NULL,
                attribute4 = NULL,
                attribute5 = NULL,
                attribute_level1 = NULL,
                attribute_level2 = NULL,
                attribute_level3 = NULL,
                attribute_level4 = NULL,
                attribute_level5 = NULL,
                item_quality = @quality,
                item_grade = 1,
                bound = 0,
                stack = 1,
                item_exp = 0,
                holy_suit_code = 0,
                holy_socket_count = 0,
                class_attribute1 = NULL,
                class_attribute2 = NULL,
                elemental_attribute1 = NULL,
                elemental_attribute2 = NULL,
                updated_at = transaction_timestamp()
            WHERE id = @itemId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @netSlot
              AND prop_id = @netItemId
              AND stack = 1
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eggItemId", checked((int)eggItemId));
        command.Parameters.AddWithValue("quality", capturedEggQuality);
        command.Parameters.AddWithValue("itemId", net.ItemId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("netSlot", checked((short)netSlot));
        command.Parameters.AddWithValue("netItemId", MysteriousTuckNetItemId);
        return await command.ExecuteScalarAsync(cancellationToken)
            as string ?? throw new InvalidDataException(
                "The final capture net was not transformed exactly once.");
    }

    private async Task<string> DecrementCaptureNetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int netSlot,
        LockedBagItem net,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET stack = stack - 1,
                updated_at = transaction_timestamp()
            WHERE id = @itemId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @netSlot
              AND prop_id = @netItemId
              AND stack = @expectedStack
              AND stack > 1
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", net.ItemId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("netSlot", checked((short)netSlot));
        command.Parameters.AddWithValue("netItemId", MysteriousTuckNetItemId);
        command.Parameters.AddWithValue("expectedStack", net.Stack);
        return await command.ExecuteScalarAsync(cancellationToken)
            as string ?? throw new InvalidDataException(
                "The capture net was not consumed exactly once.");
    }

    private async Task<CapturedEggRow> InsertCapturedEggAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int eggSlot,
        uint eggItemId,
        short capturedEggQuality,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @eggSlot, @eggItemId,
                @quality, 1, 0, 1,
                0, 0
            )
            RETURNING id, to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("eggSlot", checked((short)eggSlot));
        command.Parameters.AddWithValue("eggItemId", checked((int)eggItemId));
        command.Parameters.AddWithValue("quality", capturedEggQuality);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The captured pet egg was not inserted exactly once.");
        }

        return new(reader.GetInt64(0), reader.GetString(1));
    }

    private sealed record CapturedEggRow(long ItemId, string State);
}
