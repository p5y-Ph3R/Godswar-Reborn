using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> AdvancePetSkillCellAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.PropId is not (
                (int)PetItemCatalog.PetEnhanceSpring or
                (int)PetItemCatalog.GoldenAppleJuice) ||
            item.Stack < 1)
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }

        var pet = await LockCarriedPetSkillStateAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        if (pet is null)
        {
            return new(
                PetDurableReceiptStatus.PetNotTaken,
                KitBagSlot: bagSlot);
        }

        var current = new PetSkillSlotState(
            pet.LearnedSkillCount,
            pet.OpenedSkillCellCount,
            pet.AvailableSkillCellCount);
        if (!PetSkillSlotPolicy.TryApplyItem(
                current,
                checked((uint)item.PropId),
                out var next,
                out var rejection))
        {
            return rejection switch
            {
                PetSkillSlotTransitionRejection.MaximumSkillCellsReached =>
                    FromSkillCellPet(
                        PetDurableReceiptStatus
                            .PetSkillCellMaximumReached,
                        pet,
                        bagSlot),
                PetSkillSlotTransitionRejection.NoSealedSkillCell =>
                    FromSkillCellPet(
                        PetDurableReceiptStatus
                            .PetSkillCellNotAvailable,
                        pet,
                        bagSlot),
                PetSkillSlotTransitionRejection.UnsupportedItem =>
                    FromSkillCellPet(
                        PetDurableReceiptStatus.UnsupportedItem,
                        pet,
                        bagSlot),
                _ => throw new InvalidDataException(
                    "The carried pet has invalid skill-cell state.")
            };
        }

        var nextPetRevision = await UpdatePetSkillCellStateAsync(
            connection,
            transaction,
            characterId,
            pet,
            next,
            cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(
            connection,
            transaction,
            characterId,
            bagSlot,
            item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            characterId,
            character.InventoryRevision,
            cancellationToken);
        var isSpring =
            item.PropId == PetItemCatalog.PetEnhanceSpring;
        return new(
            isSpring
                ? PetDurableReceiptStatus.PetSkillCellMadeAvailable
                : PetDurableReceiptStatus.PetSkillCellOpened,
            KitBagSlot: bagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextPetRevision,
            IsCarried: true,
            IsSummoned: pet.IsSummoned,
            InventoryMutations:
            [
                new InventoryMutation(
                    item.ItemId,
                    consumed.MutationKind,
                    item.BeforeState,
                    consumed.AfterState,
                    isSpring
                        ? "pet_skill_cell_available"
                        : "pet_skill_cell_opened",
                    inventoryRevision)
            ]);
    }

    private async Task<LockedCarriedPetSkillState?>
        LockCarriedPetSkillStateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                pet.id,
                pet.level,
                pet.experience,
                pet.revision,
                pet.is_summoned,
                pet.opened_skill_slots,
                pet.available_skill_slots,
                (
                    SELECT count(*)::smallint
                    FROM public.character_pet_skills skill
                    WHERE skill.pet_id = pet.id
                      AND skill.is_active
                ) AS learned_skill_count
            FROM public.character_pets pet
            WHERE pet.user_id = @characterId
              AND pet.activity_state = 'owned'
              AND pet.is_carried
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var result = new LockedCarriedPetSkillState(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetBoolean(4),
            reader.GetInt16(5),
            reader.GetInt16(6),
            reader.GetInt16(7));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one carried pet is authoritative.");
        }

        return result;
    }

    private async Task<long> UpdatePetSkillCellStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedCarriedPetSkillState pet,
        PetSkillSlotState next,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET opened_skill_slots = @openedSkillCells,
                available_skill_slots = @availableSkillCells,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @expectedRevision
              AND activity_state = 'owned'
              AND is_carried
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "openedSkillCells",
            next.OpenSkillCellCount);
        command.Parameters.AddWithValue(
            "availableSkillCells",
            next.AvailableSkillCellCount);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedRevision",
            pet.Revision);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The carried pet skill-cell revision was not advanced exactly once.");
    }

    private async Task<ConsumedStackItem>
        ConsumeOneStackItemAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            int bagSlot,
            LockedBagItem item,
            CancellationToken cancellationToken)
    {
        if (item.Stack == 1)
        {
            await using var delete = CreateCommand(
                """
                DELETE FROM public.character_items
                WHERE id = @itemId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @bagSlot
                  AND prop_id = @propId
                  AND stack = 1;
                """,
                connection,
                transaction);
            AddConsumedItemParameters(
                delete,
                characterId,
                bagSlot,
                item);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The pet item was not deleted exactly once.");
            }
            return new("delete", null);
        }

        await using var update = CreateCommand(
            """
            UPDATE public.character_items
            SET stack = stack - 1,
                updated_at = transaction_timestamp()
            WHERE id = @itemId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot
              AND prop_id = @propId
              AND stack = @expectedStack
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        AddConsumedItemParameters(update, characterId, bagSlot, item);
        update.Parameters.AddWithValue("expectedStack", item.Stack);
        var afterState =
            await update.ExecuteScalarAsync(cancellationToken) as string ??
            throw new InvalidDataException(
                "The pet item stack was not decremented exactly once.");
        return new("update", afterState);
    }

    private static void AddConsumedItemParameters(
        NpgsqlCommand command,
        int characterId,
        int bagSlot,
        LockedBagItem item)
    {
        command.Parameters.AddWithValue("itemId", item.ItemId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bagSlot", (short)bagSlot);
        command.Parameters.AddWithValue("propId", item.PropId);
    }

    private static PetTransition FromSkillCellPet(
        PetDurableReceiptStatus status,
        LockedCarriedPetSkillState pet,
        int bagSlot) =>
        new(
            status,
            KitBagSlot: bagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            IsCarried: true,
            IsSummoned: pet.IsSummoned);

    private sealed record LockedCarriedPetSkillState(
        long PetId,
        short Level,
        long Experience,
        long Revision,
        bool IsSummoned,
        short OpenedSkillCellCount,
        short AvailableSkillCellCount,
        short LearnedSkillCount);

    private sealed record ConsumedStackItem(
        string MutationKind,
        string? AfterState);
}
