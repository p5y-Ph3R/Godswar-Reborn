using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetAppearanceChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetAppearanceChangeCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var bagSlot = envelope.Command.KitBagSlot;
        var pet = await LockSummonedPetForAppearanceChangeAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return await RejectPetAppearanceChangeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetAppearancePetNotSummoned,
                pet: null,
                item: null,
                target: null,
                "summoned_pet_not_found",
                cancellationToken);
        }

        var item = await LockBagItemAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            bagSlot,
            cancellationToken);
        if (item is null)
        {
            return await RejectPetAppearanceChangeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.MagicJadeNotFound,
                pet,
                item: null,
                target: null,
                "magic_jade_not_found",
                cancellationToken);
        }

        if (item.PropId <= 0 ||
            !_petContent.TryGetSpeciesByMagicJadeItemId(
                checked((uint)item.PropId),
                out var target) ||
            !_itemContent.Templates.TryGet(
                checked((uint)item.PropId),
                out var jadeTemplate))
        {
            return await RejectPetAppearanceChangeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.MagicJadeIncompatible,
                pet,
                item,
                target: null,
                "selected_item_is_not_magic_jade",
                cancellationToken);
        }

        if (!pet.IsBound)
        {
            return await RejectPetAppearanceChangeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetAppearancePetUnbound,
                pet,
                item,
                target,
                "summoned_pet_is_unbound",
                cancellationToken);
        }
        if (!pet.IsCarried || !pet.IsSummoned ||
            !string.Equals(
                pet.ActivityState,
                "owned",
                StringComparison.Ordinal) ||
            pet.ContributesToCharacter)
        {
            return await RejectPetAppearanceChangeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.PetAppearancePetUnavailable,
                pet,
                item,
                target,
                "summoned_pet_is_unavailable",
                cancellationToken);
        }
        if (pet.SpeciesId == target.SpeciesId)
        {
            return await RejectPetAppearanceChangeAsync(
                connection,
                transaction,
                envelope,
                PetDurableReceiptStatus.MagicJadeIncompatible,
                pet,
                item,
                target,
                "pet_already_has_selected_appearance",
                cancellationToken);
        }
        if (!_petContent.TryGetSpecies(pet.SpeciesId, out var oldSpecies))
        {
            throw new InvalidDataException(
                $"Summoned pet {pet.PetId} has unpublished species " +
                $"{pet.SpeciesId}.");
        }

        var nextPetRevision = await UpdatePetAppearanceAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet,
            target.SpeciesId,
            cancellationToken);
        var consumed = await ConsumeOneStackItemAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            bagSlot,
            item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            character.InventoryRevision,
            cancellationToken);
        var evidence = new PetAppearanceChangeEvidence(
            pet.SpeciesId,
            oldSpecies.DisplayName,
            target.SpeciesId,
            target.DisplayName,
            checked((uint)item.PropId),
            jadeTemplate.DisplayName,
            item.ItemId,
            bagSlot,
            _petContent.Revision.Sha256,
            _itemContent.Templates.Revision.Sha256);
        if (!evidence.IsValid)
        {
            throw new InvalidDataException(
                "The Magic Jade appearance-change evidence is invalid.");
        }

        await WritePetAppearanceChangeAuditAsync(
            connection,
            transaction,
            envelope,
            PetDurableReceiptStatus.PetAppearanceChanged,
            pet,
            item,
            target,
            evidence,
            outcome: "committed",
            reasonCode: "magic_jade_appearance_change",
            cancellationToken);
        return new PetTransition(
            PetDurableReceiptStatus.PetAppearanceChanged,
            KitBagSlot: bagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextPetRevision,
            IsCarried: true,
            IsSummoned: true,
            InventoryMutations:
            [
                new InventoryMutation(
                    item.ItemId,
                    consumed.MutationKind,
                    item.BeforeState,
                    consumed.AfterState,
                    "pet_magic_jade_consumed",
                    inventoryRevision)
            ],
            AppearanceChange: evidence);
    }

    private async Task<LockedAppearanceChangePet?>
        LockSummonedPetForAppearanceChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, species_id, level, experience, revision,
                bound, is_carried, is_summoned, activity_state,
                contributes_to_character
            FROM public.character_pets
            WHERE user_id = @characterId
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

        var pet = new LockedAppearanceChangePet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.GetString(8),
            reader.GetBoolean(9));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "A character has more than one summoned pet.");
        }
        return pet;
    }

    private async Task<long> UpdatePetAppearanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedAppearanceChangePet pet,
        short targetSpeciesId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET species_id = @targetSpeciesId,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND species_id = @oldSpeciesId
              AND revision = @expectedRevision
              AND activity_state = 'owned'
              AND bound
              AND is_carried
              AND is_summoned
              AND NOT contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("targetSpeciesId", targetSpeciesId);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("oldSpeciesId", pet.SpeciesId);
        command.Parameters.AddWithValue("expectedRevision", pet.Revision);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The pet appearance was not updated exactly once.");
    }

    private async Task<PetTransition> RejectPetAppearanceChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetAppearanceChangeCommand> envelope,
        PetDurableReceiptStatus status,
        LockedAppearanceChangePet? pet,
        LockedBagItem? item,
        PetSpeciesContentDefinition? target,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await WritePetAppearanceChangeAuditAsync(
            connection,
            transaction,
            envelope,
            status,
            pet,
            item,
            target,
            evidence: null,
            outcome: "rejected",
            reasonCode,
            cancellationToken);
        return new PetTransition(
            status,
            KitBagSlot: envelope.Command.KitBagSlot,
            PetId: pet?.PetId ?? 0,
            PetLevel: pet?.Level ?? 0,
            PetExperience: pet?.Experience ?? 0,
            PetRevision: pet?.Revision ?? 0,
            IsCarried: pet?.IsCarried ?? false,
            IsSummoned: pet?.IsSummoned ?? false);
    }

    private async Task WritePetAppearanceChangeAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetAppearanceChangeCommand> envelope,
        PetDurableReceiptStatus status,
        LockedAppearanceChangePet? pet,
        LockedBagItem? item,
        PetSpeciesContentDefinition? target,
        PetAppearanceChangeEvidence? evidence,
        string outcome,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var beforeState = pet is null
            ? null
            : JsonSerializer.Serialize(new
            {
                pet_id = pet.PetId,
                species_id = pet.SpeciesId,
                pet.Level,
                pet.Experience,
                pet_revision = pet.Revision,
                pet.IsBound,
                pet.IsCarried,
                pet.IsSummoned,
                pet.ActivityState,
                pet.ContributesToCharacter,
                pet_content_revision = _petContent.Revision.Sha256,
                item_content_revision =
                    _itemContent.Templates.Revision.Sha256
            });
        var afterState = evidence is null
            ? item is null && target is null
                ? null
                : JsonSerializer.Serialize(new
                {
                    selected_item_id = item?.PropId,
                    selected_item_instance_id = item?.ItemId,
                    selected_kit_bag_slot =
                        envelope.Command.KitBagSlot,
                    selected_item_stack = item?.Stack,
                    requested_species_id = target?.SpeciesId,
                    requested_species_name = target?.DisplayName,
                    pet_content_revision = _petContent.Revision.Sha256,
                    item_content_revision =
                        _itemContent.Templates.Revision.Sha256
                })
            : JsonSerializer.Serialize(new
            {
                pet_id = pet!.PetId,
                species_id = evidence.NewSpeciesId,
                species_name = evidence.NewSpeciesName,
                pet_revision = checked(pet.Revision + 1),
                evidence.PetContentRevision,
                evidence.ItemContentRevision
            });
        var consumedItems = evidence is null
            ? "[]"
            : JsonSerializer.Serialize(new[]
            {
                new
                {
                    item_id = evidence.MagicJadeItemId,
                    item_name = evidence.MagicJadeDisplayName,
                    item_instance_id = evidence.MagicJadeItemInstanceId,
                    quantity = 1,
                    kit_bag_slot = evidence.KitBagSlot
                }
            });

        await using var command = CreateCommand(
            """
            INSERT INTO public.pet_operation_audit (
                request_id, user_id, user_id_snapshot,
                pet_id, pet_id_snapshot, operation, outcome,
                before_state, after_state, consumed_items, reason_code
            )
            VALUES (
                @requestId, @characterId, @characterId,
                @petId, @petId, 'change_appearance', @outcome,
                @beforeState, @afterState, @consumedItems, @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "requestId",
            envelope.Command.Identity.OperationId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "petId",
            pet is null ? DBNull.Value : pet.PetId);
        command.Parameters.AddWithValue("outcome", outcome);
        AddNullableJson(command, "beforeState", beforeState);
        AddNullableJson(command, "afterState", afterState);
        command.Parameters.Add(
            "consumedItems",
            NpgsqlDbType.Jsonb).Value = consumedItems;
        command.Parameters.AddWithValue("reasonCode", reasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet appearance change was not audited exactly once.");
        }
    }

    private sealed record LockedAppearanceChangePet(
        long PetId,
        short SpeciesId,
        short Level,
        long Experience,
        long Revision,
        bool IsBound,
        bool IsCarried,
        bool IsSummoned,
        string ActivityState,
        bool ContributesToCharacter);
}
