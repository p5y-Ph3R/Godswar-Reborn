using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetSkillUnlearnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetSkillUnlearnCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockSummonedPetForSkillUnlearnAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return new(PetDurableReceiptStatus.PetNotTaken);
        }
        if (pet.OpenedSkillSlots is < 1 or
                > PetSkillSlotPolicy.MaximumLearnableSkillCells ||
            pet.AvailableSkillSlots < pet.OpenedSkillSlots ||
            pet.AvailableSkillSlots >
                PetSkillSlotPolicy.MaximumLearnableSkillCells)
        {
            throw new InvalidDataException(
                "The summoned pet skill-cell state is not canonical.");
        }
        if (envelope.Command.SkillSlot >= pet.OpenedSkillSlots)
        {
            return FromSkillUnlearnPet(
                PetDurableReceiptStatus.PetSkillNotFound,
                pet);
        }

        var potion = await LockFirstStrongPurgePotionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (potion is null)
        {
            return FromSkillUnlearnPet(
                PetDurableReceiptStatus.StrongPurgePotionNotFound,
                pet);
        }

        var skills = await LockPetSkillsAsync(
            connection,
            transaction,
            pet.PetId,
            cancellationToken);
        var selected = skills.SingleOrDefault(skill =>
            skill.IsActive &&
            skill.SlotIndex == envelope.Command.SkillSlot);
        if (selected is null)
        {
            return FromSkillUnlearnPet(
                PetDurableReceiptStatus.PetSkillNotFound,
                pet);
        }
        if (skills.Any(skill =>
                !skill.IsActive ||
                skill.SlotIndex < 0 ||
                skill.SlotIndex >= pet.OpenedSkillSlots) ||
            skills.Where(static skill => skill.IsActive)
                .Select(static skill => skill.SlotIndex)
                .Distinct()
                .Count() != skills.Count)
        {
            throw new InvalidDataException(
                "The summoned pet skill projection is not canonical.");
        }

        await DeletePetSkillAsync(
            connection,
            transaction,
            pet.PetId,
            selected,
            cancellationToken);
        await CompactPetSkillsAsync(
            connection,
            transaction,
            pet.PetId,
            skills.Where(skill => skill.SkillId != selected.SkillId)
                .OrderBy(static skill => skill.SlotIndex)
                .ToArray(),
            cancellationToken);
        var nextPetRevision = await AdvanceSkillUnlearnPetRevisionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet,
            cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            potion.BagSlot,
            potion.Item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            character.InventoryRevision,
            cancellationToken);

        return new(
            PetDurableReceiptStatus.PetSkillUnlearned,
            KitBagSlot: potion.BagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextPetRevision,
            IsCarried: true,
            IsSummoned: true,
            InventoryMutations:
            [
                new InventoryMutation(
                    potion.Item.ItemId,
                    consumed.MutationKind,
                    potion.Item.BeforeState,
                    consumed.AfterState,
                    "pet_skill_unlearn",
                    inventoryRevision)
            ]);
    }

    private async Task<LockedSkillUnlearnPet?>
        LockSummonedPetForSkillUnlearnAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, level, experience, revision,
                   opened_skill_slots, available_skill_slots
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
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

        var pet = new LockedSkillUnlearnPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt16(4),
            reader.GetInt16(5));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one summoned pet is authoritative.");
        }
        return pet;
    }

    private async Task<LockedPurgePotion?> LockFirstStrongPurgePotionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, slot_index, prop_id, item_quality, bound, stack,
                   to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @propId
              AND stack > 0
            ORDER BY slot_index
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "propId",
            checked((int)PetItemCatalog.StrongPurgePotion));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedPurgePotion(
                reader.GetInt16(1),
                new LockedBagItem(
                    reader.GetInt64(0),
                    reader.GetInt32(2),
                    reader.GetInt16(3),
                    reader.GetInt16(4) != 0,
                    reader.GetInt16(5),
                    reader.GetString(6)))
            : null;
    }

    private async Task<IReadOnlyList<LockedPetSkill>> LockPetSkillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT skill_id, slot_index, is_active, revision
            FROM public.character_pet_skills
            WHERE pet_id = @petId
            ORDER BY slot_index, skill_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        var skills = new List<LockedPetSkill>(12);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (skills.Count >= 12)
            {
                throw new InvalidDataException(
                    "The summoned pet exceeds the native skill limit.");
            }
            skills.Add(new LockedPetSkill(
                reader.GetInt32(0),
                reader.GetInt16(1),
                reader.GetBoolean(2),
                reader.GetInt64(3)));
        }
        return skills;
    }

    private async Task DeletePetSkillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        LockedPetSkill selected,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.character_pet_skills
            WHERE pet_id = @petId
              AND skill_id = @skillId
              AND slot_index = @slotIndex
              AND is_active
              AND revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("skillId", selected.SkillId);
        command.Parameters.AddWithValue("slotIndex", selected.SlotIndex);
        command.Parameters.AddWithValue("revision", selected.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The selected pet skill was not deleted exactly once.");
        }
    }

    private async Task CompactPetSkillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        IReadOnlyList<LockedPetSkill> remaining,
        CancellationToken cancellationToken)
    {
        for (var targetSlot = 0; targetSlot < remaining.Count; targetSlot++)
        {
            var skill = remaining[targetSlot];
            if (skill.SlotIndex == targetSlot)
            {
                continue;
            }

            await using var command = CreateCommand(
                """
                UPDATE public.character_pet_skills
                SET slot_index = @targetSlot,
                    revision = revision + 1
                WHERE pet_id = @petId
                  AND skill_id = @skillId
                  AND slot_index = @sourceSlot
                  AND is_active
                  AND revision = @revision;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(
                "targetSlot",
                checked((short)targetSlot));
            command.Parameters.AddWithValue("petId", petId);
            command.Parameters.AddWithValue("skillId", skill.SkillId);
            command.Parameters.AddWithValue(
                "sourceSlot",
                skill.SlotIndex);
            command.Parameters.AddWithValue("revision", skill.Revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The remaining pet skills were not compacted exactly once.");
            }
        }
    }

    private async Task<long> AdvanceSkillUnlearnPetRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedSkillUnlearnPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
              AND opened_skill_slots = @openedSkillSlots
              AND available_skill_slots = @availableSkillSlots
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("revision", pet.Revision);
        command.Parameters.AddWithValue(
            "openedSkillSlots",
            pet.OpenedSkillSlots);
        command.Parameters.AddWithValue(
            "availableSkillSlots",
            pet.AvailableSkillSlots);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The pet revision was not advanced exactly once.");
    }

    private static PetTransition FromSkillUnlearnPet(
        PetDurableReceiptStatus status,
        LockedSkillUnlearnPet pet) =>
        new(
            status,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            IsCarried: true,
            IsSummoned: true);

    private sealed record LockedSkillUnlearnPet(
        long PetId,
        short Level,
        long Experience,
        long Revision,
        short OpenedSkillSlots,
        short AvailableSkillSlots);

    private sealed record LockedPurgePotion(
        int BagSlot,
        LockedBagItem Item);

    private sealed record LockedPetSkill(
        int SkillId,
        short SlotIndex,
        bool IsActive,
        long Revision);
}
