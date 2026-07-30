using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecuteBagItemActivationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<BagItemActivationCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var command = envelope.Command;
        var item = await LockBagItemAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            command.KitBagSlot,
            cancellationToken);
        if (item is null)
        {
            return new(
                PetDurableReceiptStatus.ItemNotFound,
                KitBagSlot: command.KitBagSlot);
        }

        if (PetSpeciesCatalog.TryGetByEggItemId(
                checked((uint)item.PropId),
                out var species))
        {
            return await HatchEggAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                command.KitBagSlot,
                item,
                species,
                character,
                cancellationToken);
        }

        return await EquipBagItemAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            command.KitBagSlot,
            item,
            character,
            command.ExecutionConstraint,
            cancellationToken);
    }

    private async Task<PetTransition> EquipBagItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        LockedCharacter character,
        BagItemActivationExecutionConstraint executionConstraint,
        CancellationToken cancellationToken)
    {
        if (!EquipmentSlots.TryGetAuthoritativeSlot(
                checked((uint)item.PropId),
                out var equipmentSlot))
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }
        if (equipmentSlot is EquipmentSlots.Ring1 or EquipmentSlots.Ring2)
        {
            equipmentSlot = await ResolveRightClickRingSlotAsync(
                connection,
                transaction,
                characterId,
                equipmentSlot,
                cancellationToken);
        }
        if (equipmentSlot == EquipmentSlots.Mount &&
            executionConstraint ==
                BagItemActivationExecutionConstraint
                    .RideRuntimeBlocked)
        {
            return new(
                PetDurableReceiptStatus.EquipmentRestricted,
                KitBagSlot: bagSlot,
                EquipmentSlot: equipmentSlot);
        }
        if (equipmentSlot < 0)
        {
            return new(
                PetDurableReceiptStatus.EquipmentSlotOccupied,
                KitBagSlot: bagSlot,
                EquipmentSlot: Math.Max(-1, equipmentSlot));
        }
        if (!await ValidateAuthoritativeEquipmentEligibilityAsync(
                connection,
                transaction,
                characterId,
                item.PropId,
                equipmentSlot,
                character,
                cancellationToken))
        {
            return new(
                PetDurableReceiptStatus.EquipmentRestricted,
                KitBagSlot: bagSlot,
                EquipmentSlot: equipmentSlot);
        }

        var displaced = await LockEquipmentItemAsync(
            connection,
            transaction,
            characterId,
            equipmentSlot,
            cancellationToken);
        if (displaced is not null)
        {
            var temporarySlot = await AllocateTemporaryItemSlotAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
            _ = await MoveItemAsync(
                connection,
                transaction,
                item.ItemId,
                characterId,
                itemLocation: 2,
                temporarySlot,
                cancellationToken);
        }

        var displacedAfterState = displaced is null
            ? null
            : await MoveItemAsync(
                connection,
                transaction,
                displaced.ItemId,
                characterId,
                itemLocation: 1,
                bagSlot,
                cancellationToken);
        var afterState = await MoveItemAsync(
            connection,
            transaction,
            item.ItemId,
            characterId,
            itemLocation: 0,
            equipmentSlot,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            characterId,
            character.InventoryRevision,
            cancellationToken);
        return new(
            PetDurableReceiptStatus.EquipmentEquipped,
            KitBagSlot: bagSlot,
            EquipmentSlot: equipmentSlot,
            InventoryMutations: displaced is null
                ?
                [
                    new InventoryMutation(
                        item.ItemId,
                        "move",
                        item.BeforeState,
                        afterState,
                        "pet_equipment_equip",
                        inventoryRevision)
                ]
                :
                [
                    new InventoryMutation(
                        item.ItemId,
                        "move",
                        item.BeforeState,
                        afterState,
                        "pet_equipment_equip",
                        inventoryRevision),
                    new InventoryMutation(
                        displaced.ItemId,
                        "move",
                        displaced.BeforeState,
                        displacedAfterState ??
                            throw new InvalidDataException(
                                "The displaced equipment state is missing."),
                        "pet_equipment_displaced",
                        inventoryRevision)
                ]);
    }

    private async Task<PetTransition> HatchEggAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem egg,
        PetSpeciesDefinition species,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (egg.Stack != 1 ||
            !PetAptitudeCatalog.TryGet(
                egg.Quality,
                out var aptitudeDefinition) ||
            !PetNativeAptitudeProfileCatalog.TryGet(
                species.Type,
                aptitudeDefinition.Aptitude,
                out var nativeProfile))
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }
        if (await CountPetsAsync(
                connection,
                transaction,
                characterId,
                cancellationToken) >=
            PetManagerPlanner.MaximumOwnedPetCount)
        {
            return new(
                PetDurableReceiptStatus.PetCapacityReached,
                KitBagSlot: bagSlot);
        }

        var aptitude = aptitudeDefinition.Aptitude;
        var growth = PetGrowthPolicy.Roll(
            aptitude,
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var added = PetAddedSavvyPolicy.Roll(
            aptitude,
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var sex = (short)RandomNumberGenerator.GetInt32(2);
        var petId = await InsertPetAsync(
            connection,
            transaction,
            characterId,
            species,
            aptitude,
            added.TotalSavvy,
            sex,
            nativeProfile.Lifetime,
            egg.Bound,
            cancellationToken);
        await InsertPetStatsAsync(
            connection,
            transaction,
            petId,
            growth.BaseGrowthRates,
            added.AddedSavvy,
            cancellationToken);
        await InsertPetSkillAsync(
            connection,
            transaction,
            petId,
            species.StarterSkillId,
            cancellationToken);
        await using var consume = CreateCommand(
            """
            DELETE FROM public.character_items
            WHERE id = @itemId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot;
            """,
            connection,
            transaction);
        consume.Parameters.AddWithValue("itemId", egg.ItemId);
        consume.Parameters.AddWithValue("characterId", characterId);
        consume.Parameters.AddWithValue("bagSlot", (short)bagSlot);
        if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The hatched egg was not consumed exactly once.");
        }
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            characterId,
            character.InventoryRevision,
            cancellationToken);
        return new(
            PetDurableReceiptStatus.EggHatched,
            KitBagSlot: bagSlot,
            PetId: petId,
            PetLevel: 1,
            PetRevision: 0,
            InventoryMutations:
            [
                new InventoryMutation(
                    egg.ItemId,
                    "delete",
                    egg.BeforeState,
                    null,
                    "pet_egg_hatch",
                    inventoryRevision)
            ]);
    }

    private async Task<LockedBagItem?> LockBagItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, prop_id, item_quality, bound, stack,
                to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bagSlot", (short)bagSlot);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedBagItem(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt16(2),
                reader.GetInt16(3) != 0,
                reader.GetInt16(4),
                reader.GetString(5))
            : null;
    }

    private sealed record LockedBagItem(
        long ItemId,
        int PropId,
        short Quality,
        bool Bound,
        short Stack,
        string BeforeState);
}
