using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExpandPetShedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.PropId != PetItemCatalog.SpecialPetShed || item.Stack != 1)
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }
        if (!PetShedCapacityPolicy.IsValid(character.PetShedCapacity))
        {
            throw new InvalidDataException(
                "The locked character has an invalid pet-shed capacity.");
        }
        if (character.PetShedCapacity >=
            PetShedCapacityPolicy.MaximumOpenedCellCount)
        {
            return new(
                PetDurableReceiptStatus.PetShedMaximumReached,
                KitBagSlot: bagSlot);
        }

        var nextCapacity = checked((short)(character.PetShedCapacity + 1));
        var nextShedRevision = checked(character.PetShedRevision + 1);
        await using (var expand = CreateCommand(
            """
            UPDATE public.character_base
            SET pet_shed_capacity = @nextCapacity,
                pet_shed_revision = @nextShedRevision
            WHERE id = @characterId
              AND pet_shed_capacity = @expectedCapacity
              AND pet_shed_revision = @expectedShedRevision
            RETURNING pet_shed_capacity, pet_shed_revision;
            """,
            connection,
            transaction))
        {
            expand.Parameters.AddWithValue("characterId", characterId);
            expand.Parameters.AddWithValue(
                "expectedCapacity",
                character.PetShedCapacity);
            expand.Parameters.AddWithValue(
                "expectedShedRevision",
                character.PetShedRevision);
            expand.Parameters.AddWithValue("nextCapacity", nextCapacity);
            expand.Parameters.AddWithValue(
                "nextShedRevision",
                nextShedRevision);
            await using var reader =
                await expand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt16(0) != nextCapacity ||
                reader.GetInt64(1) != nextShedRevision ||
                await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "The pet-shed capacity revision was not advanced exactly once.");
            }
        }

        await using (var consume = CreateCommand(
            """
            DELETE FROM public.character_items
            WHERE id = @itemId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot
              AND prop_id = @propId;
            """,
            connection,
            transaction))
        {
            consume.Parameters.AddWithValue("itemId", item.ItemId);
            consume.Parameters.AddWithValue("characterId", characterId);
            consume.Parameters.AddWithValue("bagSlot", (short)bagSlot);
            consume.Parameters.AddWithValue("propId", item.PropId);
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Special Pet Shed was not consumed exactly once.");
            }
        }

        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            characterId,
            character.InventoryRevision,
            cancellationToken);
        return new(
            PetDurableReceiptStatus.PetShedExpanded,
            KitBagSlot: bagSlot,
            InventoryMutations:
            [
                new InventoryMutation(
                    item.ItemId,
                    "delete",
                    item.BeforeState,
                    null,
                    "pet_shed_expand",
                    inventoryRevision)
            ]);
    }
}
