using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ApplyPetExperienceItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        PetExperienceItemDefinition definition,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.Stack < 1 || item.PropId != definition.ItemId)
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }

        var pet = await LockCarriedPetForExperienceAsync(
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
        if (definition.RequiresBoundPet && !pet.IsBound)
        {
            return FromExperiencePet(
                PetDurableReceiptStatus.PetExperienceRestrictedPetUnbound,
                pet,
                bagSlot);
        }
        if (pet.Experience >
            PetExperienceItemPolicy.MaximumNativePetExperience -
                definition.Experience)
        {
            return FromExperiencePet(
                PetDurableReceiptStatus.PetExperienceMaximumReached,
                pet,
                bagSlot);
        }

        var nextExperience = checked(
            pet.Experience + definition.Experience);
        var nextPetRevision = await UpdatePetExperienceAsync(
            connection,
            transaction,
            characterId,
            pet,
            nextExperience,
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

        return new(
            PetDurableReceiptStatus.PetExperienceAdded,
            KitBagSlot: bagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: nextExperience,
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
                    "pet_experience_item_consumed",
                    inventoryRevision)
            ]);
    }

    private async Task<LockedExperiencePet?>
        LockCarriedPetForExperienceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, level, experience, revision, is_summoned, bound
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_carried
            ORDER BY id
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

        var pet = new LockedExperiencePet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one carried pet is authoritative.");
        }
        return pet;
    }

    private async Task<long> UpdatePetExperienceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedExperiencePet pet,
        long nextExperience,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET experience = @experience,
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
        command.Parameters.AddWithValue("experience", nextExperience);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "expectedRevision",
            pet.Revision);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The carried pet EXP revision was not advanced exactly once.");
    }

    private static PetTransition FromExperiencePet(
        PetDurableReceiptStatus status,
        LockedExperiencePet pet,
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

    private sealed record LockedExperiencePet(
        long PetId,
        short Level,
        long Experience,
        long Revision,
        bool IsSummoned,
        bool IsBound);
}
