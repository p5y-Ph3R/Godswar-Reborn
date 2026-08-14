using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task SeedPetManagerUtilityAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET bound = false,
                growth_revealed = false,
                has_soul_contract = true,
                soul_contract_stage = 6,
                contributes_to_character = false,
                current_energy = maximum_energy - 69,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned;

            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack
            )
            VALUES
                (@characterId, 1, 80, 10106, 1, 1, 0, 2),
                (@characterId, 1, 81, 10108, 1, 1, 0, 2),
                (@characterId, 1, 82, 11015, 1, 1, 0, 1);
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            4,
            await command.ExecuteNonQueryAsync(),
            "Pet Manager fixture prepares one pet and three utility items");
    }

    private static async Task SeedFullPetShedAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var petIds = new List<long>(2);
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_pets (
                user_id, species_id, name, sex, level, experience,
                aptitude, remaining_lifetime, bound, activity_state,
                growth_revealed, growth_activation_policy_version,
                is_carried, is_summoned, contributes_to_character,
                initial_savvy_baseline_total,
                rarity_added_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                completed_pet_merges,
                talent_mask, has_owner_merge_talent,
                birth_rank, hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision
            )
            SELECT
                source.user_id, source.species_id,
                'Utility Shed Full ' || ordinal::text,
                source.sex, source.level, source.experience,
                source.aptitude, source.remaining_lifetime,
                source.bound, 'owned',
                source.growth_revealed,
                source.growth_activation_policy_version,
                false, false, false,
                source.initial_savvy_baseline_total,
                source.rarity_added_savvy_baseline_total,
                source.initial_savvy_policy_version,
                source.rarity_added_savvy_policy_version,
                source.initial_savvy_source_version,
                source.completed_pet_merges,
                source.talent_mask, source.has_owner_merge_talent,
                source.birth_rank, source.hatch_rank_roll,
                source.hatch_rank_outcome_order,
                source.hatch_rank_content_revision
            FROM public.character_pets source
            CROSS JOIN generate_series(1, 2) ordinal
            WHERE source.user_id = @characterId
              AND source.activity_state = 'sealed'
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                petIds.Add(reader.GetInt64(0));
            }
        }
        Check.Equal(
            2,
            petIds.Count,
            "Unseal fixture creates two complete shed occupants");
        await using (var stats = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id, stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration, revision,
                birth_initial_savvy, rarity_added_savvy
            )
            SELECT
                target.pet_id, source.stat_code,
                source.initial_savvy, source.added_savvy,
                source.base_growth_rate, source.growth_acceleration,
                source.revision, source.birth_initial_savvy,
                source.rarity_added_savvy
            FROM unnest(@petIds::bigint[]) target(pet_id)
            CROSS JOIN public.character_pet_stat_values source
            JOIN public.character_pets source_pet
              ON source_pet.id = source.pet_id
            WHERE source_pet.user_id = @characterId
              AND source_pet.activity_state = 'sealed';
            """,
            connection,
            transaction))
        {
            stats.Parameters.AddWithValue("characterId", characterId);
            stats.Parameters.AddWithValue("petIds", petIds.ToArray());
            Check.Equal(
                12,
                await stats.ExecuteNonQueryAsync(),
                "Unseal fixture clones six Savvy/Growth rows per occupant");
        }
        await transaction.CommitAsync();
    }

    private static async Task DeleteFullPetShedAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM public.character_pets
            WHERE user_id = @characterId
              AND name LIKE 'Utility Shed Full %';
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            2,
            await command.ExecuteNonQueryAsync(),
            "Unseal fixture frees both temporary shed cells");
    }

    private static async Task<PetManagerUtilityState>
        ReadPetManagerUtilityStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.activity_state,
                pet.is_carried,
                pet.is_summoned,
                pet.bound,
                pet.sex,
                pet.revision,
                pet.current_energy,
                pet.maximum_energy,
                character.inventory_revision,
                (SELECT COALESCE(sum(item.stack), 0)::integer
                 FROM public.character_items item
                 WHERE item.user_id = @characterId
                   AND item.prop_id = 10106),
                (SELECT COALESCE(sum(item.stack), 0)::integer
                 FROM public.character_items item
                 WHERE item.user_id = @characterId
                   AND item.prop_id = 10108),
                (SELECT count(*)::integer
                 FROM public.character_items item
                 WHERE item.user_id = @characterId
                   AND item.prop_id = 10109),
                (SELECT count(*)::integer
                 FROM public.sealed_pet_items link
                 WHERE link.owner_character_id = @characterId),
                (SELECT COALESCE(max(link.item_instance_id), 0)
                 FROM public.sealed_pet_items link
                 WHERE link.owner_character_id = @characterId),
                (SELECT COALESCE(max(item.slot_index), -1)::integer
                 FROM public.character_items item
                 WHERE item.user_id = @characterId
                   AND item.prop_id = 10109)
            FROM public.character_pets pet
            JOIN public.character_base character
              ON character.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet Manager utility state is missing.");
        }
        return new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            checked((byte)reader.GetInt16(4)),
            reader.GetInt64(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.GetInt32(14));
    }

    private static async Task AssertPetManagerGenderAndClaimsAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long petId)
    {
        await using (var prepare = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET bound = true,
                is_carried = true,
                is_summoned = true,
                contributes_to_character = false,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND activity_state = 'owned';
            """))
        {
            prepare.Parameters.AddWithValue("petId", petId);
            prepare.Parameters.AddWithValue(
                "characterId",
                subject.CharacterId);
            Check.Equal(
                1,
                await prepare.ExecuteNonQueryAsync(),
                "gender fixture binds and summons the unsealed pet");
        }
        var beforeGender = await ReadPetManagerUtilityStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var genderEnvelope = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.ChangeGender);
        var gender = await executor.ExecuteAsync(genderEnvelope);
        var genderReplay = await restarted.ExecuteAsync(genderEnvelope);
        var afterGender = await ReadPetManagerUtilityStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            gender.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            genderReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            genderReplay.Receipt == gender.Receipt &&
            gender.Receipt?.Status ==
                PetDurableReceiptStatus.PetGenderChanged &&
            gender.Receipt.PetManagerUtility is
            {
                ItemTemplateId: 11015,
                BeforePetState: { } genderBefore,
                AfterPetState: { } genderAfter
            } &&
            genderBefore.Sex == beforeGender.Sex &&
            genderAfter.Sex == afterGender.Sex &&
            genderAfter.Sex == 1 - genderBefore.Sex &&
            genderAfter.Revision == genderBefore.Revision + 1 &&
            gender.Receipt.PetRevision == genderAfter.Revision,
            "Gender consumes once, toggles sex, advances exactly one revision, and replays");

        var callEnvelope = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.ClaimPetCall);
        var call = await executor.ExecuteAsync(callEnvelope);
        var callReplay = await restarted.ExecuteAsync(callEnvelope);
        var callEvidence = call.Receipt?.PetManagerUtility;
        Check.True(
            call.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            callReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            callReplay.Receipt == call.Receipt &&
            call.Receipt?.Status ==
                PetDurableReceiptStatus.PetCallClaimed &&
            call.Receipt.PetId == 0 &&
            call.Receipt.PetRevision == 0 &&
            callEvidence is
            {
                PetId: 0,
                ItemTemplateId: 11003,
                ItemInstanceId: > 0,
                KitBagSlot: >= 0
            },
            "Pet Call claim is pet-independent and stores exact created-item evidence");
        await MoveClaimedCharmOutsideBagAsync(
            dataSource,
            subject.CharacterId,
            callEvidence!.ItemInstanceId);

        var secondCallEnvelope = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.ClaimPetCall);
        var secondCall = await executor.ExecuteAsync(secondCallEnvelope);
        var secondCallReplay =
            await restarted.ExecuteAsync(secondCallEnvelope);
        Check.True(
            secondCall.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            secondCall.Receipt?.Status ==
                PetDurableReceiptStatus.PetManagerClaimAlreadyHeld &&
            secondCallReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            secondCallReplay.Receipt == secondCall.Receipt &&
            await CountUtilityItemAsync(
                dataSource,
                subject.CharacterId,
                11003) == 1,
            "moving Pet Call outside the bag cannot evade claim uniqueness");

        var mergeEnvelope = CreatePetManagerUtilityEnvelope(
            subject,
            correlation,
            PetManagerUtilityOperation.ClaimMerge);
        var merge = await executor.ExecuteAsync(mergeEnvelope);
        var mergeReplay = await restarted.ExecuteAsync(mergeEnvelope);
        Check.True(
            merge.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            mergeReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            mergeReplay.Receipt == merge.Receipt &&
            merge.Receipt?.Status ==
                PetDurableReceiptStatus.PetMergeClaimed &&
            merge.Receipt.PetId == 0 &&
            merge.Receipt.PetRevision == 0 &&
            await CountUtilityItemAsync(
                dataSource,
                subject.CharacterId,
                11004) == 1,
            "Merge claim commits once with no fabricated pet revision");
    }

    private static async Task MoveClaimedCharmOutsideBagAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long itemId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_items
            SET item_location = 2,
                slot_index = -1,
                updated_at = transaction_timestamp()
            WHERE id = @itemId
              AND user_id = @characterId
              AND prop_id = 11003;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "claim fixture moves Pet Call outside the bag");
    }

    private static async Task<int> CountUtilityItemAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        int itemTemplateId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.character_items
            WHERE user_id = @characterId
              AND prop_id = @itemTemplateId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemTemplateId", itemTemplateId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertPetManagerUtilityAuditAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                count(*)::integer,
                count(*) FILTER (WHERE outcome = 'committed')::integer,
                count(*) FILTER (WHERE outcome = 'rejected')::integer,
                count(*) FILTER (
                    WHERE outcome = 'committed'
                      AND before_state IS DISTINCT FROM after_state
                )::integer,
                count(*) FILTER (
                    WHERE outcome = 'rejected'
                      AND reason_code IS NOT NULL
                )::integer,
                (SELECT count(*)::integer
                 FROM public.command_inbox inbox
                 WHERE inbox.aggregate_key = @aggregateKey
                   AND inbox.command_family = 'pet_manager_utility'),
                (SELECT COALESCE(sum(inbox.duplicate_count), 0)::integer
                 FROM public.command_inbox inbox
                 WHERE inbox.aggregate_key = @aggregateKey
                   AND inbox.command_family = 'pet_manager_utility')
            FROM public.pet_operation_audit audit
            WHERE audit.user_id_snapshot = @characterId
              AND audit.operation IN (
                  'check_growth', 'seal', 'unseal',
                  'claim_pet_call', 'claim_merge', 'change_gender'
              );
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "aggregateKey",
            PetDurablePersistenceCodec.AggregateKey(characterId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "Pet Manager utility audit summary exists");
        Check.True(
            reader.GetInt32(0) == 9 &&
            reader.GetInt32(1) == 7 &&
            reader.GetInt32(2) == 2 &&
            reader.GetInt32(3) == 7 &&
            reader.GetInt32(4) == 2 &&
            reader.GetInt32(5) == 9 &&
            reader.GetInt32(6) == 9,
            "every utility outcome has one immutable audit/inbox row, mutable successes differ before/after, and nine replays add no transition audit");
        await AssertUnsealEnergyAuditAsync(dataSource, characterId);
    }

    private sealed record PetManagerUtilityState(
        string ActivityState,
        bool IsCarried,
        bool IsSummoned,
        bool IsBound,
        byte Sex,
        long PetRevision,
        int CurrentEnergy,
        int MaximumEnergy,
        long InventoryRevision,
        int PixieTears,
        int EmptySealJades,
        int PackedSealJades,
        int SealedLinks,
        long PackedItemId,
        int PackedSlot);
}
