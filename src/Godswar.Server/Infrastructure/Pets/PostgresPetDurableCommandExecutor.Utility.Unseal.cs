using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecuteUnsealAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var item = await LockBagItemAsync(
            connection, transaction, envelope.Subject.CharacterId,
            envelope.Command.KitBagSlot, cancellationToken);
        if (item is null || item.PropId != PetItemCatalog.PackedSealJade ||
            item.Stack != 1)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerMalformedSelection,
                kitBagSlot: envelope.Command.KitBagSlot);
        }
        var link = await LockSealedPetLinkAsync(
            connection, transaction, envelope.Subject.CharacterId,
            item.ItemId, cancellationToken);
        if (link is null)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerSealedLinkInvalid,
                kitBagSlot: envelope.Command.KitBagSlot);
        }
        if (await CountOwnedUtilityPetsAsync(
                connection, transaction, envelope.Subject.CharacterId,
                cancellationToken) >= character.PetShedCapacity)
        {
            var linkPet = await LockUtilityPetByIdAsync(
                connection, transaction, envelope.Subject.CharacterId,
                link.PetId, cancellationToken);
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerPetUnavailable,
                linkPet,
                envelope.Command.KitBagSlot);
        }

        var pet = await LockUtilityPetByIdAsync(
            connection, transaction, envelope.Subject.CharacterId,
            link.PetId, cancellationToken) ??
            throw new InvalidDataException(
                "The linked sealed pet does not exist for its owner.");
        if (await LockActiveOwnerMergePetIdAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                cancellationToken) is not null)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerPetUnavailable,
                pet,
                envelope.Command.KitBagSlot);
        }
        await ClearPreviousUnsealPresenceAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet.PetId,
            cancellationToken);
        var revision = await MarkPetUnsealedAsync(
            connection, transaction, envelope.Subject.CharacterId,
            pet, cancellationToken);
        await DeleteSealedPetLinkAsync(
            connection, transaction, link.LinkId, cancellationToken);
        await DeletePackedSealAsync(
            connection, transaction, envelope.Subject.CharacterId,
            envelope.Command.KitBagSlot, item.ItemId,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection, transaction, envelope.Subject.CharacterId,
            character.InventoryRevision, cancellationToken);
        var evidence = UtilityEvidence(
            envelope.Command.Operation,
            pet,
            item.PropId,
            item.ItemId,
            envelope.Command.KitBagSlot,
            beforeState: pet.State(),
            afterState: pet.State(
                revision,
                activityState: "owned",
                isCarried: true,
                isSummoned: true,
                contributesToCharacter: false,
                currentEnergy: pet.MaximumEnergy));
        return UtilityTransition(
            PetDurableReceiptStatus.PetUnsealed,
            evidence,
            pet with
            {
                IsCarried = true,
                IsSummoned = true,
                CurrentEnergy = pet.MaximumEnergy,
                Revision = revision
            },
            envelope.Command.KitBagSlot,
            revision,
            [new InventoryMutation(
                item.ItemId,
                "delete",
                item.BeforeState,
                null,
                "pet_unsealed_from_jade",
                inventoryRevision)]);
    }

    private async Task<LockedSealedPetLink?> LockSealedPetLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT link.id, link.pet_id
            FROM public.sealed_pet_items link
            JOIN public.character_items item
              ON item.id = link.item_instance_id
            JOIN public.character_pets pet
              ON pet.id = link.pet_id
            WHERE link.item_instance_id = @itemId
              AND link.owner_character_id = @characterId
              AND item.user_id = @characterId
              AND item.prop_id = 10109
              AND (item.bound <> 0) = link.pet_bound_snapshot
              AND pet.user_id = @characterId
              AND pet.activity_state = 'sealed'
              AND pet.bound = link.pet_bound_snapshot
              AND NOT pet.is_carried
              AND NOT pet.is_summoned
              AND NOT pet.contributes_to_character
            FOR UPDATE OF link, item, pet;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private sealed record LockedSealedPetLink(long LinkId, long PetId);

    private async Task<int> CountOwnedUtilityPetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<LockedUtilityPet?> LockUtilityPetByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, species_id, name, sex, level, experience,
                bound, is_carried, is_summoned,
                contributes_to_character, activity_state, revision,
                growth_revealed, has_soul_contract, soul_contract_stage,
                current_energy, maximum_energy
            FROM public.character_pets
            WHERE id = @petId
              AND user_id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedUtilityPet(
                reader.GetInt64(0), reader.GetInt16(1),
                reader.GetString(2), checked((byte)reader.GetInt16(3)),
                reader.GetInt16(4), reader.GetInt64(5),
                reader.GetBoolean(6), reader.GetBoolean(7),
                reader.GetBoolean(8), reader.GetBoolean(9),
                reader.GetString(10), reader.GetInt64(11),
                reader.GetBoolean(12), reader.GetBoolean(13),
                checked((byte)reader.GetInt16(14)),
                reader.GetInt32(15), reader.GetInt32(16))
            : null;
    }

    private async Task<long> MarkPetUnsealedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedUtilityPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET activity_state = 'owned',
                is_carried = true,
                is_summoned = true,
                contributes_to_character = false,
                current_energy = maximum_energy,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND activity_state = 'sealed'
              AND NOT is_carried
              AND NOT is_summoned
              AND NOT contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        AddUtilityPetRevisionParameters(command, characterId, pet);
        return RequireNextRevision(
            await command.ExecuteScalarAsync(cancellationToken),
            pet.Revision,
            "unsealed pet");
    }

    private async Task ClearPreviousUnsealPresenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long unsealedPetId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = false,
                is_summoned = false,
                contributes_to_character = false,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE user_id = @characterId
              AND id <> @unsealedPetId
              AND activity_state = 'owned'
              AND NOT contributes_to_character
              AND (is_carried OR is_summoned)
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("unsealedPetId", unsealedPetId);
        var cleared = new List<long>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cleared.Add(reader.GetInt64(0));
        }
        if (cleared.Count > 1)
        {
            throw new InvalidDataException(
                "More than one prior carried pet was cleared by Unseal.");
        }
    }

    private async Task DeleteSealedPetLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long linkId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.sealed_pet_items
            WHERE id = @linkId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("linkId", linkId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The active sealed-pet link was not removed exactly once.");
        }
    }
}
