using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> LearnReviewedPetSkillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        LockedBagItem item,
        PetSkillBookActivationDefinition book,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        if (item.Stack < 1 || item.PropId != checked((int)book.ItemId))
        {
            return new(
                PetDurableReceiptStatus.UnsupportedItem,
                KitBagSlot: bagSlot);
        }

        var pet = await LockCarriedPetForSkillBookAsync(
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

        var skills = await LockSkillBookPetSkillsAsync(
            connection,
            transaction,
            pet.PetId,
            cancellationToken);
        ValidateSkillBookPetState(pet, skills);
        if (!_petContent.TryGetSpecies(pet.SpeciesId, out var species) ||
            !_learnedSkillContent.TryGetCurveByRuntimeSkillId(
                species.StarterSkillId,
                out var speciesCurve))
        {
            throw new InvalidDataException(
                "The carried pet species has no pinned skill-family authority.");
        }
        if (speciesCurve.FamilyType != book.FamilyType)
        {
            return FromSkillBookPet(
                PetDurableReceiptStatus.PetSkillBookWrongSpecies,
                pet,
                bagSlot);
        }

        var familySkills = skills.Where(skill =>
        {
            return _learnedSkillContent.TryGetCurveByRuntimeSkillId(
                    skill.SkillId,
                    out var curve) &&
                curve.FamilyType == book.FamilyType;
        }).ToArray();
        if (familySkills.Length > 1)
        {
            throw new InvalidDataException(
                "The carried pet has duplicate rows for one skill family.");
        }
        var current = familySkills.SingleOrDefault();
        var currentPriority = current?.SkillRank ?? 0;
        var traits = await LockSkillBookPetTraitsAsync(
            connection,
            transaction,
            pet,
            cancellationToken);
        if (!PetLearnedSkillResolver.CanLearn(
                _learnedSkillContent,
                book.FamilyType,
                book.Priority,
                currentPriority,
                traits,
                out var rejection))
        {
            return FromSkillBookPet(
                ToSkillBookStatus(rejection),
                pet,
                bagSlot);
        }

        var skillSlot = current?.SlotIndex ?? FindFirstOpenSkillSlot(
            pet,
            skills);
        if (skillSlot < 0)
        {
            return FromSkillBookPet(
                PetDurableReceiptStatus.PetSkillBookNoOpenSlot,
                pet,
                bagSlot);
        }

        if (current is null)
        {
            await InsertLearnedPetSkillAsync(
                connection,
                transaction,
                pet.PetId,
                book,
                checked((short)skillSlot),
                cancellationToken);
        }
        else
        {
            await UpgradeLearnedPetSkillAsync(
                connection,
                transaction,
                pet.PetId,
                current,
                book,
                cancellationToken);
        }

        var nextPetRevision = await AdvanceSkillBookPetRevisionAsync(
            connection,
            transaction,
            characterId,
            pet,
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
        var evidence = new PetSkillLearnEvidence(
            pet.PetId,
            item.ItemId,
            book.ItemId,
            pet.SpeciesId,
            book.FamilyType,
            checked((short)currentPriority),
            book.Priority,
            current?.SkillId ?? 0,
            book.RuntimeSkillId,
            checked((short)skillSlot),
            book.TraitRequirement,
            new PetContentStatVector(
                traits.Agility,
                traits.Strength,
                traits.Accuracy,
                traits.Technique,
                traits.Wisdom,
                traits.Luck),
            _itemContent.Templates.Revision.Sha256,
            _learnedSkillContent.Revision.Sha256);
        if (!evidence.IsValid)
        {
            throw new InvalidDataException(
                "The committed pet skill evidence is invalid.");
        }

        return new(
            PetDurableReceiptStatus.PetSkillLearned,
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
                    "pet_skill_book_learn",
                    inventoryRevision)
            ],
            SkillLearn: evidence);
    }

    private async Task<LockedSkillBookPet?>
        LockCarriedPetForSkillBookAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, species_id, level, experience, revision,
                   is_summoned, opened_skill_slots,
                   available_skill_slots, initial_savvy_source_version
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_carried
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
        var pet = new LockedSkillBookPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            reader.GetInt16(6),
            reader.GetInt16(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one carried pet is authoritative.");
        }
        return pet;
    }

    private async Task<IReadOnlyList<LockedSkillBookSkill>>
        LockSkillBookPetSkillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT skill_id, slot_index, skill_rank,
                   skill_experience, is_active, revision
            FROM public.character_pet_skills
            WHERE pet_id = @petId
            ORDER BY slot_index, skill_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        var skills = new List<LockedSkillBookSkill>(12);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (skills.Count >= PetSkillSlotPolicy.MaximumLearnableSkillCells)
            {
                throw new InvalidDataException(
                    "The carried pet exceeds the native skill limit.");
            }
            skills.Add(new LockedSkillBookSkill(
                reader.GetInt32(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetInt32(3),
                reader.GetBoolean(4),
                reader.GetInt64(5)));
        }
        return skills;
    }

    private void ValidateSkillBookPetState(
        LockedSkillBookPet pet,
        IReadOnlyList<LockedSkillBookSkill> skills)
    {
        var slots = new HashSet<short>();
        var state = new PetSkillSlotState(
            checked((short)skills.Count),
            pet.OpenedSkillSlots,
            pet.AvailableSkillSlots);
        if (!PetSkillSlotPolicy.IsValid(state) ||
            skills.Any(skill =>
                !skill.IsActive ||
                skill.SkillRank < 1 ||
                skill.SkillExperience < 0 ||
                skill.SlotIndex < 0 ||
                skill.SlotIndex >= pet.OpenedSkillSlots ||
                !slots.Add(skill.SlotIndex)))
        {
            throw new InvalidDataException(
                "The carried pet skill projection is not canonical.");
        }
        foreach (var skill in skills)
        {
            if (_learnedSkillContent.TryGetCurveByRuntimeSkillId(
                    skill.SkillId,
                    out var curve) &&
                (curve.FirstRuntimeSkillId != skill.SkillId ||
                 curve.Priority != skill.SkillRank))
            {
                throw new InvalidDataException(
                    "A learned pet skill does not match its pinned tier.");
            }
        }
    }

    private async Task<PetSavvy> LockSkillBookPetTraitsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockedSkillBookPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT stat_code, initial_savvy, added_savvy,
                   base_growth_rate, growth_acceleration,
                   birth_initial_savvy, rarity_added_savvy
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        var rows = new List<LockedSkillBookTrait>(6);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LockedSkillBookTrait(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6)));
        }
        return ResolveSkillBookTraits(pet, rows);
    }

    private static PetSavvy ResolveSkillBookTraits(
        LockedSkillBookPet pet,
        IReadOnlyList<LockedSkillBookTrait> rows)
    {
        PetSavvyRuntimeSemantics.ValidateProjectionSourceVersion(
            pet.InitialSavvySourceVersion);
        if (rows.Count != 6 ||
            rows.Where((row, index) => row.StatCode != index + 1).Any())
        {
            throw new InvalidDataException(
                "The carried pet has no complete Savvy projection.");
        }
        var initial = ToSavvy(rows.Select(static row => row.Initial));
        var added = ToSavvy(rows.Select(static row => row.Added));
        var growth = ToSavvy(rows.Select(static row => row.Growth));
        var acceleration = ToSavvy(
            rows.Select(static row => row.Acceleration));
        var hasCurrentProvenance =
            pet.InitialSavvySourceVersion is not null;
        if (rows.Any(row => hasCurrentProvenance
                ? row.Birth is null || row.Rarity is null ||
                  row.Birth <= 0m || row.Birth != row.Rarity
                : row.Birth is not null || row.Rarity is not null))
        {
            throw new InvalidDataException(
                "The carried pet has partial Savvy provenance.");
        }
        PetSavvy? rarity = hasCurrentProvenance
            ? ToSavvy(rows.Select(static row => row.Rarity!.Value))
            : null;
        return PetSavvyRuntimeSemantics.ResolvePlayerVisibleTotal(
            pet.Level,
            initial,
            added,
            growth,
            acceleration,
            rarity);
    }

    private static PetSavvy ToSavvy(IEnumerable<decimal> values)
    {
        var ordered = values.ToArray();
        return ordered.Length == 6
            ? new PetSavvy(
                ordered[0], ordered[1], ordered[2],
                ordered[3], ordered[4], ordered[5])
            : throw new InvalidDataException(
                "A pet Savvy vector is incomplete.");
    }

    private static int FindFirstOpenSkillSlot(
        LockedSkillBookPet pet,
        IReadOnlyList<LockedSkillBookSkill> skills)
    {
        var state = new PetSkillSlotState(
            checked((short)skills.Count),
            pet.OpenedSkillSlots,
            pet.AvailableSkillSlots);
        if (!PetSkillSlotPolicy.CanLearnSkill(state))
        {
            return -1;
        }
        var occupied = skills.Select(static skill => skill.SlotIndex)
            .ToHashSet();
        return Enumerable.Range(0, pet.OpenedSkillSlots)
            .First(slot => !occupied.Contains(checked((short)slot)));
    }

    private async Task InsertLearnedPetSkillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        PetSkillBookActivationDefinition book,
        short skillSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_pet_skills (
                pet_id, skill_id, slot_index, skill_rank,
                skill_experience, is_active, revision
            )
            VALUES (
                @petId, @skillId, @slotIndex, @priority,
                0, true, 0
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("skillId", book.RuntimeSkillId);
        command.Parameters.AddWithValue("slotIndex", skillSlot);
        command.Parameters.AddWithValue("priority", book.Priority);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The learned pet skill was not inserted exactly once.");
        }
    }

    private async Task UpgradeLearnedPetSkillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        LockedSkillBookSkill current,
        PetSkillBookActivationDefinition book,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pet_skills
            SET skill_id = @newSkillId,
                skill_rank = @newPriority,
                skill_experience = 0,
                revision = revision + 1
            WHERE pet_id = @petId
              AND skill_id = @oldSkillId
              AND slot_index = @slotIndex
              AND skill_rank = @oldPriority
              AND skill_experience = @oldExperience
              AND is_active
              AND revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("newSkillId", book.RuntimeSkillId);
        command.Parameters.AddWithValue("newPriority", book.Priority);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("oldSkillId", current.SkillId);
        command.Parameters.AddWithValue("slotIndex", current.SlotIndex);
        command.Parameters.AddWithValue("oldPriority", current.SkillRank);
        command.Parameters.AddWithValue(
            "oldExperience",
            current.SkillExperience);
        command.Parameters.AddWithValue("revision", current.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The learned pet skill tier was not advanced exactly once.");
        }
    }

    private async Task<long> AdvanceSkillBookPetRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedSkillBookPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND species_id = @speciesId
              AND revision = @revision
              AND activity_state = 'owned'
              AND is_carried
              AND opened_skill_slots = @openedSkillSlots
              AND available_skill_slots = @availableSkillSlots
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("speciesId", pet.SpeciesId);
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
                "The pet skill-book revision was not advanced exactly once.");
    }

    private static PetDurableReceiptStatus ToSkillBookStatus(
        PetSkillLearnRejection rejection) => rejection switch
        {
            PetSkillLearnRejection.AlreadyLearned =>
                PetDurableReceiptStatus.PetSkillBookAlreadyLearned,
            PetSkillLearnRejection.PriorTierRequired =>
                PetDurableReceiptStatus.PetSkillBookPriorTierRequired,
            PetSkillLearnRejection.TraitRequirementNotMet =>
                PetDurableReceiptStatus
                    .PetSkillBookTraitRequirementNotMet,
            _ => PetDurableReceiptStatus.PetSkillBookInvalidState
        };

    private static PetTransition FromSkillBookPet(
        PetDurableReceiptStatus status,
        LockedSkillBookPet pet,
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

}
