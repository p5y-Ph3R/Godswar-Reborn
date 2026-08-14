using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const short StrongPurgePotionSlot = 85;
    private const short StrongPurgePotionStack = 3;
    private const int RemovedSkillId = 910_001;
    private const int CompactedSkillId = 910_002;

    private static async Task AssertPetSkillUnlearnAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long petId)
    {
        await SeedPetSkillUnlearnStateAsync(
            dataSource,
            subject.CharacterId,
            petId);

        var notSummonedBefore = await ReadPetSkillUnlearnStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var notSummoned = await executor.ExecuteAsync(
            CreatePetSkillUnlearnEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                skillSlot: 1));
        Check.True(
            notSummoned.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            notSummoned.Receipt?.Status ==
                PetDurableReceiptStatus.PetNotTaken,
            "skill removal requires one authoritative summoned pet");
        AssertPetSkillUnlearnValueUnchanged(
            notSummonedBefore,
            await ReadPetSkillUnlearnStateAsync(
                dataSource,
                subject.CharacterId,
                petId),
            "not-summoned skill removal");

        await SetPetSkillUnlearnSummonedAsync(dataSource, petId, true);
        await AssertLockedPetSkillSlotRejectedAsync(
            dataSource,
            executor,
            subject,
            correlation,
            petId);

        var noPotionBefore = await ReadPetSkillUnlearnStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var noPotion = await executor.ExecuteAsync(
            CreatePetSkillUnlearnEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                skillSlot: 1));
        Check.True(
            noPotion.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            noPotion.Receipt?.Status ==
                PetDurableReceiptStatus.StrongPurgePotionNotFound,
            "skill removal rejects a missing Strong Purge Potion");
        AssertPetSkillUnlearnValueUnchanged(
            noPotionBefore,
            await ReadPetSkillUnlearnStateAsync(
                dataSource,
                subject.CharacterId,
                petId),
            "missing-potion skill removal");

        await SeedStrongPurgePotionAsync(
            dataSource,
            subject.CharacterId);
        var emptySlotBefore = await ReadPetSkillUnlearnStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var emptySlot = await executor.ExecuteAsync(
            CreatePetSkillUnlearnEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                skillSlot: 5));
        Check.True(
            emptySlot.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            emptySlot.Receipt?.Status ==
                PetDurableReceiptStatus.PetSkillNotFound,
            "skill removal rejects an empty chosen slot");
        AssertPetSkillUnlearnValueUnchanged(
            emptySlotBefore,
            await ReadPetSkillUnlearnStateAsync(
                dataSource,
                subject.CharacterId,
                petId),
            "empty-slot skill removal");

        var operationId = Guid.NewGuid();
        var envelope = CreatePetSkillUnlearnEnvelope(
            subject,
            correlation,
            operationId,
            skillSlot: 1);
        var committed = await executor.ExecuteAsync(envelope);
        var replayed = await restarted.ExecuteAsync(envelope);
        Check.True(
            committed.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            committed.Receipt?.Status ==
                PetDurableReceiptStatus.PetSkillUnlearned &&
            replayed.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replayed.Receipt == committed.Receipt,
            "skill removal commits once and replays its exact receipt after restart");

        var after = await ReadPetSkillUnlearnStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            after.PetRevision == emptySlotBefore.PetRevision + 1 &&
            after.OpenedSkillSlots == emptySlotBefore.OpenedSkillSlots &&
            after.AvailableSkillSlots ==
                emptySlotBefore.AvailableSkillSlots &&
            after.IsCarried &&
            after.IsSummoned &&
            after.PotionStack == StrongPurgePotionStack - 1 &&
            after.InventoryRevision ==
                emptySlotBefore.InventoryRevision + 1 &&
            after.UnlearnInventoryLedgerCount == 1 &&
            after.UnlearnInventoryOutboxCount == 1,
            "skill removal consumes exactly one potion and advances pet and inventory revisions once");
        Check.True(
            after.Skills.Length == 2 &&
            !after.Skills.Any(value => value.StartsWith(
                $"{RemovedSkillId}:",
                StringComparison.Ordinal)) &&
            after.Skills.Any(value => string.Equals(
                value,
                $"{CompactedSkillId}:1:1",
                StringComparison.Ordinal)),
            "skill removal deletes the selected skill and compacts later skills left");
        Check.True(
            committed.Receipt is
            {
                Family: CommandFamily.PetSkillUnlearn,
                KitBagSlot: StrongPurgePotionSlot,
                IsCarried: true,
                IsSummoned: true,
                OutboxEventId: not null
            } &&
            committed.Receipt.PetId == petId &&
            committed.Receipt.PetRevision == after.PetRevision &&
            committed.Receipt.AggregateRevision ==
                after.PetStreamRevision,
            "skill removal receipt identifies its authoritative pet, potion slot, revision, and outbox event");
        Check.True(
            after.UnlearnAuditCount == 5 &&
            after.UnlearnInboxCount == 5 &&
            after.UnlearnPetOutboxCount == 1 &&
            after.UnlearnDuplicateCount == 1 &&
            after.UnlearnOutcomes.SequenceEqual(
                new[]
                {
                    "rejected",
                    "rejected",
                    "rejected",
                    "rejected",
                    "committed"
                }),
            "skill removal retains rejection, commit, replay, audit, inbox, and outbox evidence");

        await SetPetSkillUnlearnSummonedAsync(dataSource, petId, false);
    }

    private static CommandEnvelope<PetSkillUnlearnCommand>
        CreatePetSkillUnlearnEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            Guid operationId,
            int skillSlot) =>
        PlayerOwnershipTestFences.Bind(
            PetSkillUnlearnCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetSkillUnlearnCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        operationId,
                        correlation.ConnectionId),
                    skillSlot)));

    private static async Task SeedPetSkillUnlearnStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var pet = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET activity_state = 'owned',
                is_carried = true,
                is_summoned = false,
                contributes_to_character = false,
                opened_skill_slots = 6,
                available_skill_slots = 6
            WHERE id = @petId
              AND user_id = @characterId;
            """,
            connection,
            transaction))
        {
            pet.Parameters.AddWithValue("petId", petId);
            pet.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                1,
                await pet.ExecuteNonQueryAsync(),
                "skill-unlearn fixture prepares one carried pet");
        }
        await using (var clearSkills = new NpgsqlCommand(
            """
            DELETE FROM public.character_pet_skills
            WHERE pet_id = @petId
              AND slot_index > 0;
            """,
            connection,
            transaction))
        {
            clearSkills.Parameters.AddWithValue("petId", petId);
            await clearSkills.ExecuteNonQueryAsync();
        }
        await using (var skills = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_skills (
                pet_id, skill_id, slot_index, skill_rank,
                skill_experience, is_active, revision
            )
            VALUES
                (@petId, @removedSkillId, 1, 1, 0, true, 0),
                (@petId, @compactedSkillId, 2, 1, 0, true, 0);
            """,
            connection,
            transaction))
        {
            skills.Parameters.AddWithValue("petId", petId);
            skills.Parameters.AddWithValue("removedSkillId", RemovedSkillId);
            skills.Parameters.AddWithValue(
                "compactedSkillId",
                CompactedSkillId);
            Check.Equal(
                2,
                await skills.ExecuteNonQueryAsync(),
                "skill-unlearn fixture inserts two later skills");
        }
        await using (var potion = new NpgsqlCommand(
            """
            DELETE FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @propId;
            """,
            connection,
            transaction))
        {
            potion.Parameters.AddWithValue("characterId", characterId);
            potion.Parameters.AddWithValue(
                "propId",
                checked((int)PetItemCatalog.StrongPurgePotion));
            await potion.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task SeedStrongPurgePotionAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slot, @propId,
                0, 1, 1, @stack, 0, 0
            );
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", StrongPurgePotionSlot);
        command.Parameters.AddWithValue(
            "propId",
            checked((int)PetItemCatalog.StrongPurgePotion));
        command.Parameters.AddWithValue("stack", StrongPurgePotionStack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "skill-unlearn fixture inserts one potion stack");
    }

    private static async Task SetPetSkillUnlearnSummonedAsync(
        NpgsqlDataSource dataSource,
        long petId,
        bool isSummoned)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = true,
                is_summoned = @isSummoned,
                contributes_to_character = false
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("isSummoned", isSummoned);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "skill-unlearn fixture updates summon state");
    }

    private static async Task<PetSkillUnlearnState>
        ReadPetSkillUnlearnStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long petId)
    {
        const string family = "pet_skill_unlearn";
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.revision,
                pet.opened_skill_slots,
                pet.available_skill_slots,
                pet.is_carried,
                pet.is_summoned,
                ARRAY(
                    SELECT concat_ws(
                        ':', skill.skill_id, skill.slot_index,
                        skill.revision)
                    FROM public.character_pet_skills skill
                    WHERE skill.pet_id = pet.id
                      AND skill.is_active
                    ORDER BY skill.slot_index
                ),
                COALESCE((
                    SELECT item.stack
                    FROM public.character_items item
                    WHERE item.user_id = @characterId
                      AND item.item_location = 1
                      AND item.prop_id = @potionId
                    ORDER BY item.slot_index
                    LIMIT 1
                ), 0),
                character_row.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.reason_code = 'pet_skill_unlearn'
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events event
                    JOIN public.command_inbox inbox
                      ON inbox.id = event.command_inbox_id
                    WHERE event.aggregate_type = 'character_inventory'
                      AND inbox.command_family = @family
                ),
                stream.current_version,
                (
                    SELECT count(*)
                    FROM public.command_audit audit
                    WHERE audit.aggregate_type = 'character_pet_value'
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @family
                ),
                (
                    SELECT count(*)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_type = 'character_pet_value'
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @family
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events event
                    WHERE event.aggregate_type = 'character_pet_value'
                      AND event.aggregate_key = @aggregateKey
                      AND event.event_type = 'pet.skill_unlearned'
                ),
                (
                    SELECT COALESCE(sum(inbox.duplicate_count), 0)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_type = 'character_pet_value'
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @family
                ),
                ARRAY(
                    SELECT audit.outcome_code
                    FROM public.command_audit audit
                    WHERE audit.aggregate_type = 'character_pet_value'
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @family
                    ORDER BY audit.id
                )
            FROM public.character_pets pet
            JOIN public.character_base character_row
              ON character_row.id = pet.user_id
            JOIN public.pet_durable_stream_versions stream
              ON stream.character_id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "potionId",
            checked((int)PetItemCatalog.StrongPurgePotion));
        command.Parameters.AddWithValue("family", family);
        command.Parameters.AddWithValue(
            "aggregateKey",
            PetDurablePersistenceCodec.AggregateKey(characterId));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet skill-unlearn state disappeared.");
        }
        return new PetSkillUnlearnState(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetFieldValue<string[]>(5),
            reader.GetInt16(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetFieldValue<string[]>(15));
    }

    private static void AssertPetSkillUnlearnValueUnchanged(
        PetSkillUnlearnState before,
        PetSkillUnlearnState after,
        string scope)
    {
        Check.True(
            after.PetRevision == before.PetRevision &&
            after.OpenedSkillSlots == before.OpenedSkillSlots &&
            after.AvailableSkillSlots == before.AvailableSkillSlots &&
            after.IsCarried == before.IsCarried &&
            after.IsSummoned == before.IsSummoned &&
            after.Skills.SequenceEqual(before.Skills) &&
            after.PotionStack == before.PotionStack &&
            after.InventoryRevision == before.InventoryRevision &&
            after.UnlearnInventoryLedgerCount ==
                before.UnlearnInventoryLedgerCount &&
            after.UnlearnInventoryOutboxCount ==
                before.UnlearnInventoryOutboxCount &&
            after.PetStreamRevision == before.PetStreamRevision &&
            after.UnlearnPetOutboxCount ==
                before.UnlearnPetOutboxCount,
            $"{scope} preserves all pet and inventory value state");
    }

    private sealed record PetSkillUnlearnState(
        long PetRevision,
        short OpenedSkillSlots,
        short AvailableSkillSlots,
        bool IsCarried,
        bool IsSummoned,
        string[] Skills,
        short PotionStack,
        long InventoryRevision,
        long UnlearnInventoryLedgerCount,
        long UnlearnInventoryOutboxCount,
        long PetStreamRevision,
        long UnlearnAuditCount,
        long UnlearnInboxCount,
        long UnlearnPetOutboxCount,
        long UnlearnDuplicateCount,
        string[] UnlearnOutcomes);
}
