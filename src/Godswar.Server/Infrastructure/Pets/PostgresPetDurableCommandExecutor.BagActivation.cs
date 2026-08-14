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

        if (_petContent.TryGetSpeciesByEggItemId(
                checked((uint)item.PropId),
                out var species))
        {
            return await ExecuteWithBagConsumableCooldownAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                command.KitBagSlot,
                item,
                activationCancellationToken => HatchEggAsync(
                    connection,
                    transaction,
                    envelope.Subject.CharacterId,
                    command.KitBagSlot,
                    item,
                    species,
                    character,
                    activationCancellationToken),
                cancellationToken);
        }

        if (item.PropId == PetItemCatalog.SpecialPetShed)
        {
            return await ExecuteWithBagConsumableCooldownAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                command.KitBagSlot,
                item,
                activationCancellationToken => ExpandPetShedAsync(
                    connection,
                    transaction,
                    envelope.Subject.CharacterId,
                    command.KitBagSlot,
                    item,
                    character,
                    activationCancellationToken),
                cancellationToken);
        }

        if (item.PropId is
                (int)PetItemCatalog.PetEnhanceSpring or
                (int)PetItemCatalog.GoldenAppleJuice)
        {
            return await ExecuteWithBagConsumableCooldownAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                command.KitBagSlot,
                item,
                activationCancellationToken => AdvancePetSkillCellAsync(
                    connection,
                    transaction,
                    envelope.Subject.CharacterId,
                    command.KitBagSlot,
                    item,
                    character,
                    activationCancellationToken),
                cancellationToken);
        }

        if (PetExperienceItemPolicy.TryResolve(
                _itemContent.Templates,
                checked((uint)item.PropId),
                out var experienceItem))
        {
            return await ExecuteWithBagConsumableCooldownAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                command.KitBagSlot,
                item,
                activationCancellationToken => ApplyPetExperienceItemAsync(
                    connection,
                    transaction,
                    envelope.Subject.CharacterId,
                    command.KitBagSlot,
                    item,
                    experienceItem,
                    character,
                    activationCancellationToken),
                cancellationToken);
        }

        if (PetSkillBookActivationPolicy.IsReviewedItem(
                checked((uint)item.PropId)))
        {
            if (!PetSkillBookActivationPolicy.TryResolve(
                    _itemContent.Templates,
                    _learnedSkillContent,
                    checked((uint)item.PropId),
                    out var book))
            {
                return new(
                    PetDurableReceiptStatus.UnsupportedItem,
                    KitBagSlot: command.KitBagSlot);
            }
            return await ExecuteWithBagConsumableCooldownAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                command.KitBagSlot,
                item,
                activationCancellationToken =>
                    LearnReviewedPetSkillAsync(
                        connection,
                        transaction,
                        envelope.Subject.CharacterId,
                        command.KitBagSlot,
                        item,
                        book,
                        character,
                        activationCancellationToken),
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
                _itemContent.Templates,
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
        PetSpeciesContentDefinition species,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (egg.Stack != 1 ||
            !_petContent.TryGetAptitude(
                egg.Quality,
                out var aptitudeDefinition) ||
            !_petContent.TryGetNativeProfile(
                species.SpeciesId,
                aptitudeDefinition.Aptitude,
                out var nativeProfile))
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
        var ownedPetCount = await CountPetsAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
        if (ownedPetCount >= character.PetShedCapacity ||
            ownedPetCount >= _petContent.Settings.MaximumOwnedPetCount)
        {
            return new(
                PetDurableReceiptStatus.PetCapacityReached,
                KitBagSlot: bagSlot);
        }

        var aptitude = aptitudeDefinition.Aptitude;
        var hatchRank = PetHatchRankEvidence.Create(
            _petContent.RollHatchRank(
                aptitude,
                _petHatchRankRollSource.NextRoll()),
            _petContent.Revision.Sha256);
        // New pets begin with an effective Weak Growth vector. Their egg
        // quality remains authoritative for Savvy and for the later Phoenix
        // Feather reroll; high-tier Growth must not affect level-ups early.
        var growth = _petContent.RollGrowth(
            checked((short)PetAptitude.Weak),
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var savvy = _petContent.RollInitialSavvy(
            aptitude,
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var sex = (short)RandomNumberGenerator.GetInt32(2);
        var initialSkillSlots = PetSkillSlotPolicy.CreateHatchState(
            (PetAptitude)aptitude).OpenSkillCellCount;
        var preserveSummonedCompanion =
            await ClearOtherCarriedPetsAsync(
                connection,
                transaction,
                characterId,
                petId: 0,
                cancellationToken);
        var petId = await InsertPetAsync(
            connection,
            transaction,
            characterId,
            species,
            aptitude,
            hatchRank,
            savvy.TotalSavvy,
            sex,
            nativeProfile.Lifetime,
            egg.Bound,
            aptitudeDefinition.InnateTalentMask,
            initialSkillSlots,
            isCarried: true,
            isSummoned: preserveSummonedCompanion,
            cancellationToken: cancellationToken);
        await InsertPetStatsAsync(
            connection,
            transaction,
            petId,
            ToPetSavvy(savvy.Values),
            ToPetSavvy(growth.Rates),
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
            IsCarried: true,
            IsSummoned: preserveSummonedCompanion,
            HatchRank: hatchRank,
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
