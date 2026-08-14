using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecuteSealAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockSummonedUtilityPetAsync(
            connection, transaction, envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerPetNotSummoned);
        }
        if (pet.ContributesToCharacter || pet.ActivityState != "owned")
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerPetUnavailable,
                pet);
        }
        var empty = await LockFirstUtilityItemAsync(
            connection, transaction, envelope.Subject.CharacterId,
            checked((int)PetItemCatalog.EmptySealJade),
            cancellationToken);
        if (empty is null)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerMaterialNotFound,
                pet);
        }
        var packedSlot = empty.Item.Stack == 1
            ? empty.BagSlot
            : await LockFirstEmptyUtilityBagSlotAsync(
                connection, transaction, envelope.Subject.CharacterId,
                cancellationToken);
        if (!packedSlot.HasValue)
        {
            return RejectUtility(
                envelope.Command.Operation,
                PetDurableReceiptStatus.PetManagerBagFull,
                pet);
        }

        var mutations = new List<InventoryMutation>(2);
        CreatedUtilityItem packed;
        // A singleton empty jade becomes the packed jade in place. Only a
        // stacked material needs a new singleton item for its pet link.
        if (empty.Item.Stack == 1)
        {
            packed = await ReplaceEmptySealWithPackedAsync(
                connection, transaction, envelope.Subject.CharacterId,
                empty, pet.IsBound, cancellationToken);
            mutations.Add(new InventoryMutation(
                packed.ItemId,
                "update",
                empty.Item.BeforeState,
                packed.AfterState,
                "pet_sealed_into_jade",
                0));
        }
        else
        {
            var consumed = await ConsumeOneStackItemAsync(
                connection, transaction, envelope.Subject.CharacterId,
                empty.BagSlot, empty.Item, cancellationToken);
            packed = await InsertUtilityItemAsync(
                connection, transaction, envelope.Subject.CharacterId,
                packedSlot.Value,
                checked((int)PetItemCatalog.PackedSealJade),
                cancellationToken,
                pet.IsBound);
            mutations.Add(new InventoryMutation(
                empty.Item.ItemId,
                consumed.MutationKind,
                empty.Item.BeforeState,
                consumed.AfterState,
                "pet_empty_seal_consumed",
                0));
            mutations.Add(new InventoryMutation(
                packed.ItemId,
                "add",
                null,
                packed.AfterState,
                "pet_sealed_into_jade",
                0));
        }
        var revision = await MarkPetSealedAsync(
            connection, transaction, envelope.Subject.CharacterId,
            pet, cancellationToken);
        await InsertSealedPetLinkAsync(
            connection, transaction, envelope,
            pet, packed.ItemId, cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection, transaction, envelope.Subject.CharacterId,
            character.InventoryRevision, cancellationToken);
        for (var index = 0; index < mutations.Count; index++)
        {
            mutations[index] = mutations[index] with
            {
                InventoryRevision = inventoryRevision
            };
        }
        var evidence = UtilityEvidence(
            envelope.Command.Operation,
            pet,
            checked((int)PetItemCatalog.PackedSealJade),
            packed.ItemId,
            packedSlot.Value,
            beforeState: pet.State(),
            afterState: pet.State(
                revision,
                activityState: "sealed",
                isCarried: false,
                isSummoned: false,
                contributesToCharacter: false,
                hasSoulContract: false,
                soulContractStage: 0));
        return UtilityTransition(
            PetDurableReceiptStatus.PetSealed,
            evidence,
            pet with
            {
                IsCarried = false,
                IsSummoned = false,
                ContributesToCharacter = false,
                ActivityState = "sealed",
                Revision = revision,
                HasSoulContract = false,
                SoulContractStage = 0
            },
            packedSlot.Value,
            revision,
            mutations);
    }

    private async Task<long> MarkPetSealedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedUtilityPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET activity_state = 'sealed',
                is_carried = false,
                is_summoned = false,
                contributes_to_character = false,
                has_soul_contract = false,
                soul_contract_stage = 0,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
              AND NOT contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        AddUtilityPetRevisionParameters(command, characterId, pet);
        return RequireNextRevision(
            await command.ExecuteScalarAsync(cancellationToken),
            pet.Revision,
            "sealed pet");
    }

    private async Task InsertSealedPetLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetManagerUtilityCommand> envelope,
        LockedUtilityPet pet,
        long itemId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.sealed_pet_items (
                item_instance_id, pet_id, owner_character_id,
                seal_request_id, item_instance_id_snapshot,
                pet_id_snapshot, owner_character_id_snapshot,
                pet_species_id_snapshot, pet_name_snapshot,
                pet_bound_snapshot
            )
            VALUES (
                @itemId, @petId, @characterId,
                @requestId, @itemId, @petId, @characterId,
                @speciesId, @petName, @petBound
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "requestId",
            envelope.Command.Identity.OperationId);
        command.Parameters.AddWithValue("speciesId", pet.SpeciesId);
        command.Parameters.AddWithValue("petName", pet.Name);
        command.Parameters.AddWithValue("petBound", pet.IsBound);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The active sealed-pet link was not created once.");
        }
    }
}
