using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertPetSoulContractAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            PetAptitude.Smart);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var hatch = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                BagItemActivationCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new BagItemActivationCommand(
                        PetCommandOperationIdentity.SecureClient(
                            Guid.NewGuid()),
                        fixture.EggSlot))));
        var petId = hatch.Receipt?.PetId ??
            throw new InvalidDataException(
                "Soul Contract fixture failed to hatch.");
        var summoned = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetPresenceTransitionCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    new PetPresenceTransitionCommand(
                        PetCommandOperationIdentity.SecureClient(
                            Guid.NewGuid()),
                        petId,
                        PetPresenceCommandOperation.CallOut))));
        Check.True(
            summoned.Receipt is
            {
                Status: PetDurableReceiptStatus.PresenceChanged,
                IsSummoned: true
            },
            "Soul Contract fixture summons its target");
        await SeedSoulContractMaterialsAsync(
            dataSource,
            fixture.CharacterId);

        var before = await ReadSoulContractStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        var zero = await executor.ExecuteAsync(
            CreateSoulContractEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                quantity: 0));
        var afterZero = await ReadSoulContractStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            zero.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            zero.Receipt is
            {
                Status: PetDurableReceiptStatus.PetSoulContractSigned,
                KitBagSlot: -1,
                SoulContract:
                {
                    PreviousStage: 0,
                    NewStage: 1,
                    MaterialTemplateId: 10105,
                    MaterialQuantity: 0,
                    BasicSavvyIncreaseHundredths: 300
                }
            } &&
            afterZero.Stage == 1 && afterZero.HasLegacyFlag &&
            afterZero.PetRevision == before.PetRevision + 1 &&
            afterZero.InventoryRevision == before.InventoryRevision &&
            afterZero.MaterialCount == before.MaterialCount &&
            afterZero.ConsumedStackCount == 0 &&
            afterZero.ConsumedQuantity == 0 &&
            afterZero.InventoryLedgerCount == 0 &&
            afterZero.SelectedMaterialId == 10105 &&
            afterZero.SelectedQuantity == 0 &&
            SameSoulGameplayState(before, afterZero),
            "q0 Soul Contract stores stage one without mutating pet progression, raw Savvy, growth, rank, or player vitals");

        var operationId = Guid.NewGuid();
        var fiveEnvelope = CreateSoulContractEnvelope(
            subject,
            correlation,
            operationId,
            quantity: 5);
        var five = await executor.ExecuteAsync(fiveEnvelope);
        var replay = await restarted.ExecuteAsync(fiveEnvelope);
        var afterFive = await ReadSoulContractStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            five.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == five.Receipt &&
            five.Receipt is
            {
                Status: PetDurableReceiptStatus.PetSoulContractSigned,
                SoulContract:
                {
                    PreviousStage: 1,
                    NewStage: 6,
                    MaterialQuantity: 5,
                    BasicSavvyIncreaseHundredths: 800
                }
            } &&
            afterFive.Stage == 6 && afterFive.HasLegacyFlag &&
            afterFive.PetRevision == afterZero.PetRevision + 1 &&
            afterFive.InventoryRevision ==
                afterZero.InventoryRevision + 1 &&
            afterFive.MaterialCount == 0 &&
            afterFive.ConsumedStackCount == 2 &&
            afterFive.ConsumedQuantity == 5 &&
            afterFive.InventoryLedgerCount == 2 &&
            afterFive.SelectedMaterialId == 10105 &&
            afterFive.SelectedQuantity == 5 &&
            afterFive.SoulAuditCount == 2 &&
            afterFive.CommandAuditCount == 2 &&
            afterFive.CommandInboxCount == 2 &&
            // q0 emits one pet outbox; q5 emits pet plus inventory outboxes.
            afterFive.CommandOutboxCount == 3 &&
            SameSoulGameplayState(before, afterFive),
            "re-sign replaces stage one with six, consumes once, and preserves pet progression, raw Savvy, growth, rank, and player vitals");

        var conflict = await restarted.ExecuteAsync(
            CreateSoulContractEnvelope(
                subject,
                correlation,
                operationId,
                quantity: 4));
        var afterConflict = await ReadSoulContractStateAsync(
            dataSource,
            fixture.CharacterId,
            petId);
        Check.True(
            conflict.Disposition ==
                PetDurableExecutionDisposition.RequestHashConflict &&
            afterConflict.DuplicateCount ==
                afterFive.DuplicateCount &&
            afterConflict.ConflictCount ==
                afterFive.ConflictCount + 1 &&
            SameSoulCommit(afterFive, afterConflict),
            "one Soul Contract UUID cannot authorize another quantity");
    }

    private static CommandEnvelope<PetSoulContractCommand>
        CreateSoulContractEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            Guid operationId,
            int quantity) =>
        PlayerOwnershipTestFences.Bind(
            PetSoulContractCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetSoulContractCommand(
                    PetCommandOperationIdentity.SecureClient(operationId),
                    PetSoulContractPolicy.ContractSpiritItemId,
                    quantity)));

    private static async Task SeedSoulContractMaterialsAsync(
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
            VALUES
                (@characterId, 1, 83, 10105, 0, 1, 0, 2, 0, 0),
                (@characterId, 1, 84, 10105, 0, 1, 0, 3, 0, 0);
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            2,
            await command.ExecuteNonQueryAsync(),
            "Soul Contract fixture inserts split Contract Spirit stacks");
    }

    private static async Task<SoulContractDatabaseState>
        ReadSoulContractStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.soul_contract_stage,
                pet.has_soul_contract,
                pet.revision,
                character_row.inventory_revision,
                COALESCE((SELECT sum(stack)
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND prop_id = 10105), 0),
                ARRAY(SELECT initial_savvy
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id ORDER BY stat_code),
                ARRAY(SELECT added_savvy
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id ORDER BY stat_code),
                pet.level,
                pet.experience,
                pet.rank,
                ARRAY(SELECT base_growth_rate
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id ORDER BY stat_code),
                ARRAY(SELECT growth_acceleration
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id ORDER BY stat_code),
                character_row."curHP",
                character_row."curMP",
                character_row."MaxHP",
                character_row."MaxMP",
                character_row.vitals_revision,
                (SELECT count(*) FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'soul_contract'),
                COALESCE((SELECT jsonb_array_length(consumed_items)
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'soul_contract'
                      AND outcome = 'committed'
                    ORDER BY id DESC LIMIT 1), 0),
                COALESCE((SELECT sum((entry->>'quantity')::integer)
                    FROM public.pet_operation_audit audit
                    CROSS JOIN LATERAL
                        jsonb_array_elements(audit.consumed_items) entry
                    WHERE audit.user_id_snapshot = @characterId
                      AND audit.operation = 'soul_contract'), 0),
                COALESCE((SELECT
                    (before_state->>'selected_material_template_id')::integer
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'soul_contract'
                    ORDER BY id DESC LIMIT 1), -1),
                COALESCE((SELECT
                    (before_state->>'selected_material_quantity')::integer
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'soul_contract'
                    ORDER BY id DESC LIMIT 1), -1),
                (SELECT count(*) FROM public.command_audit
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_family = 'pet_soul_contract'),
                (SELECT count(*) FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_family = 'pet_soul_contract'),
                (SELECT count(*) FROM public.outbox_events outbox
                    JOIN public.command_inbox inbox
                      ON inbox.id = outbox.command_inbox_id
                    WHERE inbox.aggregate_type = 'character_pet_value'
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'pet_soul_contract'),
                (SELECT count(*) FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                      AND reason_code = 'pet_soul_contract'),
                COALESCE((SELECT sum(duplicate_count)
                    FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_family = 'pet_soul_contract'), 0),
                COALESCE((SELECT sum(request_conflict_count)
                    FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_family = 'pet_soul_contract'), 0)
            FROM public.character_pets pet
            JOIN public.character_base character_row
              ON character_row.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Soul Contract fixture state disappeared.");
        }
        return new SoulContractDatabaseState(
            checked((byte)reader.GetInt16(0)),
            reader.GetBoolean(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetFieldValue<decimal[]>(5),
            reader.GetFieldValue<decimal[]>(6),
            reader.GetInt16(7),
            reader.GetInt64(8),
            reader.GetDecimal(9),
            reader.GetFieldValue<decimal[]>(10),
            reader.GetFieldValue<decimal[]>(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt64(16),
            reader.GetInt64(17),
            reader.GetInt32(18),
            reader.GetInt64(19),
            reader.GetInt32(20),
            reader.GetInt32(21),
            reader.GetInt64(22),
            reader.GetInt64(23),
            reader.GetInt64(24),
            reader.GetInt64(25),
            reader.GetInt64(26),
            reader.GetInt64(27));
    }

    private static bool SameSoulGameplayState(
        SoulContractDatabaseState first,
        SoulContractDatabaseState second) =>
        first.PetLevel == second.PetLevel &&
        first.PetExperience == second.PetExperience &&
        first.PetRank == second.PetRank &&
        first.CurrentHp == second.CurrentHp &&
        first.CurrentMp == second.CurrentMp &&
        first.BaseMaxHp == second.BaseMaxHp &&
        first.BaseMaxMp == second.BaseMaxMp &&
        first.VitalsRevision == second.VitalsRevision &&
        first.InitialSavvy.SequenceEqual(second.InitialSavvy) &&
        first.AddedSavvy.SequenceEqual(second.AddedSavvy) &&
        first.BaseGrowthRates.SequenceEqual(second.BaseGrowthRates) &&
        first.GrowthAcceleration.SequenceEqual(second.GrowthAcceleration);

    private static bool SameSoulCommit(
        SoulContractDatabaseState expected,
        SoulContractDatabaseState actual) =>
        expected with
        {
            InitialSavvy = actual.InitialSavvy,
            AddedSavvy = actual.AddedSavvy,
            BaseGrowthRates = actual.BaseGrowthRates,
            GrowthAcceleration = actual.GrowthAcceleration,
            ConflictCount = actual.ConflictCount
        } == actual &&
        SameSoulGameplayState(expected, actual);

    private sealed record SoulContractDatabaseState(
        byte Stage,
        bool HasLegacyFlag,
        long PetRevision,
        long InventoryRevision,
        long MaterialCount,
        decimal[] InitialSavvy,
        decimal[] AddedSavvy,
        short PetLevel,
        long PetExperience,
        decimal PetRank,
        decimal[] BaseGrowthRates,
        decimal[] GrowthAcceleration,
        int CurrentHp,
        int CurrentMp,
        int BaseMaxHp,
        int BaseMaxMp,
        long VitalsRevision,
        long SoulAuditCount,
        int ConsumedStackCount,
        long ConsumedQuantity,
        int SelectedMaterialId,
        int SelectedQuantity,
        long CommandAuditCount,
        long CommandInboxCount,
        long CommandOutboxCount,
        long InventoryLedgerCount,
        long DuplicateCount,
        long ConflictCount);
}
