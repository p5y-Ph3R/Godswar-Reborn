using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task<PetFixture> CreatePetRebirthFixtureAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        await using var store = new PostgresGameStore(
            connectionString,
            itemContent,
            petContent: PetContentBaseline.Create());
        var account = await store.LoginOrCreateAccountAsync(
            $"b12_rebirth_{token}",
            string.Empty);
        var character = await store.CreateCharacterAsync(
            account.Id,
            new GameCharacter
            {
                Name = $"Rebirth{token}",
                Camp = GameDefaults.SpartaCamp,
                Profession = 0,
                Level = 80
            });
        const int eggSlot = 90;
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slot, 10150,
                6, 1, 1, 1, 0, 0
            );
            """,
            connection))
        {
            command.Parameters.AddWithValue(
                "characterId",
                character.Id);
            command.Parameters.AddWithValue("slot", (short)eggSlot);
            Check.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                "rebirth fixture inserts one egg without global reseeding");
        }
        await using var transaction =
            await connection.BeginTransactionAsync();
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            account.Id,
            character.Id);
        await transaction.CommitAsync();
        return new PetFixture(account.Id, character.Id, eggSlot);
    }

    private static CommandEnvelope<BagItemActivationCommand>
        CreateRebirthFixtureHatchEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            int bagSlot) =>
        PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        Guid.NewGuid(),
                        correlation.ConnectionId),
                    bagSlot)));

    private static CommandEnvelope<PetRebirthCommand>
        CreatePetRebirthEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            Guid operationId,
            int materialTemplateId,
            int quantity = PetRebirthSpiritPolicy.MaximumCount) =>
        PlayerOwnershipTestFences.Bind(
            PetRebirthCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new PetRebirthCommand(
                    PetCommandOperationIdentity.SecureClient(operationId),
                    materialTemplateId,
                    quantity)));

    private static async Task SeedPetRebirthAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var pet = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET level = 120,
                experience = 12345,
                completed_rebirths = 0,
                rebirths_remaining = 1,
                has_soul_contract = true,
                bound = false,
                activity_state = 'owned',
                is_carried = true,
                is_summoned = true,
                contributes_to_character = false,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId;

            UPDATE public.character_pet_stat_values
            SET added_savvy =
                    (base_growth_rate + growth_acceleration) * 120,
                revision = revision + 1
            WHERE pet_id = @petId;
            """,
            connection,
            transaction))
        {
            pet.Parameters.AddWithValue("petId", petId);
            pet.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                7,
                await pet.ExecuteNonQueryAsync(),
                "rebirth fixture prepares one eligible pet and six scaled Added values");
        }
        await using (var items = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES
                (@characterId, 1, @firstSlot, @standardItem,
                 0, 1, 0, 3, 0, 0),
                (@characterId, 1, @secondSlot, @standardItem,
                 0, 1, 0, 2, 0, 0),
                (@characterId, 1, @restrictedSlot, @restrictedItem,
                 0, 1, 1, 5, 0, 0);
            """,
            connection,
            transaction))
        {
            items.Parameters.AddWithValue("characterId", characterId);
            items.Parameters.AddWithValue(
                "firstSlot",
                RebirthSpiritFirstSlot);
            items.Parameters.AddWithValue(
                "secondSlot",
                RebirthSpiritSecondSlot);
            items.Parameters.AddWithValue(
                "restrictedSlot",
                RestrictedRebirthSpiritSlot);
            items.Parameters.AddWithValue(
                "standardItem",
                checked((int)PetItemCatalog.RebirthSpirit));
            items.Parameters.AddWithValue(
                "restrictedItem",
                checked((int)PetItemCatalog.RebornHarpyia));
            Check.Equal(
                3,
                await items.ExecuteNonQueryAsync(),
                "rebirth fixture inserts split standard and restricted materials");
        }
        await transaction.CommitAsync();
    }

    private static async Task<PetRebirthState> ReadPetRebirthStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.level,
                pet.experience,
                pet.completed_rebirths,
                pet.rebirths_remaining,
                pet.revision,
                character_row.inventory_revision,
                COALESCE((SELECT sum(stack)
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND prop_id = @standardItem), 0),
                COALESCE((SELECT sum(stack)
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND prop_id = @restrictedItem), 0),
                ARRAY(SELECT added_savvy
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id ORDER BY stat_code),
                ARRAY(SELECT base_growth_rate
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id ORDER BY stat_code),
                ARRAY(SELECT growth_acceleration
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id ORDER BY stat_code),
                ARRAY(SELECT revision
                    FROM public.character_pet_stat_values
                    WHERE pet_id = pet.id ORDER BY stat_code),
                (SELECT count(*) FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'),
                (SELECT count(*) FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'committed'),
                (SELECT count(*) FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'rejected'),
                COALESCE((SELECT jsonb_array_length(consumed_items)
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'committed'), 0),
                COALESCE((SELECT sum((entry->>'quantity')::integer)
                    FROM public.pet_operation_audit audit
                    CROSS JOIN LATERAL
                        jsonb_array_elements(audit.consumed_items) entry
                    WHERE audit.user_id_snapshot = @characterId
                      AND audit.operation = 'rebirth'
                      AND audit.outcome = 'committed'), 0),
                (SELECT count(*) FROM public.command_audit
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_family = 'pet_rebirth'),
                (SELECT count(*) FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_family = 'pet_rebirth'),
                (SELECT count(*) FROM public.outbox_events outbox
                    JOIN public.command_inbox inbox
                      ON inbox.id = outbox.command_inbox_id
                    WHERE inbox.aggregate_type = 'character_pet_value'
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'pet_rebirth'),
                (SELECT count(*) FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                      AND reason_code = 'pet_rebirth'),
                (SELECT count(*) FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                      AND reason_code = 'pet_rebirth'
                      AND inventory_revision =
                          character_row.inventory_revision),
                COALESCE((SELECT sum(duplicate_count)
                    FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_family = 'pet_rebirth'), 0),
                COALESCE((SELECT sum(request_conflict_count)
                    FROM public.command_inbox
                    WHERE aggregate_type = 'character_pet_value'
                      AND aggregate_key = @aggregateKey
                      AND command_family = 'pet_rebirth'), 0),
                COALESCE((SELECT
                    (before_state->>'selected_material_template_id')::integer
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'committed'
                    ORDER BY id DESC LIMIT 1), -1),
                COALESCE((SELECT
                    (before_state->>'selected_material_quantity')::integer
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'committed'
                    ORDER BY id DESC LIMIT 1), -1),
                COALESCE((SELECT
                    (after_state->>'surplus_level_count')::integer
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'committed'
                    ORDER BY id DESC LIMIT 1), -1),
                COALESCE((SELECT
                    (after_state->>'carried_experience')::bigint
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'committed'
                    ORDER BY id DESC LIMIT 1), -1),
                COALESCE((SELECT
                    (after_state->>'historical_surplus_experience')::bigint
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'committed'
                    ORDER BY id DESC LIMIT 1), -1),
                COALESCE((SELECT
                    (after_state->>'pre_rebirth_unspent_experience')::bigint
                    FROM public.pet_operation_audit
                    WHERE user_id_snapshot = @characterId
                      AND operation = 'rebirth'
                      AND outcome = 'committed'
                    ORDER BY id DESC LIMIT 1), -1)
            FROM public.character_pets pet
            JOIN public.character_base character_row
              ON character_row.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "standardItem",
            checked((int)PetItemCatalog.RebirthSpirit));
        command.Parameters.AddWithValue(
            "restrictedItem",
            checked((int)PetItemCatalog.RebornHarpyia));
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The pet rebirth fixture state disappeared.");
        }
        return new(
            reader.GetInt16(0),
            reader.GetInt64(1),
            reader.GetInt16(2),
            reader.GetInt16(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetFieldValue<decimal[]>(8),
            reader.GetFieldValue<decimal[]>(9),
            reader.GetFieldValue<decimal[]>(10),
            reader.GetFieldValue<long[]>(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetInt32(15),
            reader.GetInt64(16),
            reader.GetInt64(17),
            reader.GetInt64(18),
            reader.GetInt64(19),
            reader.GetInt64(20),
            reader.GetInt64(21),
            reader.GetInt64(22),
            reader.GetInt64(23),
            reader.GetInt32(24),
            reader.GetInt32(25),
            reader.GetInt32(26),
            reader.GetInt64(27),
            reader.GetInt64(28),
            reader.GetInt64(29));
    }

    private static void AssertPetRebirthValueUnchanged(
        PetRebirthState before,
        PetRebirthState after,
        string phase)
    {
        Check.True(
            before.Level == after.Level &&
            before.Experience == after.Experience &&
            before.CompletedRebirths == after.CompletedRebirths &&
            before.RebirthsRemaining == after.RebirthsRemaining &&
            before.PetRevision == after.PetRevision &&
            before.InventoryRevision == after.InventoryRevision &&
            before.StandardSpiritCount == after.StandardSpiritCount &&
            before.RestrictedSpiritCount == after.RestrictedSpiritCount &&
            before.AddedSavvy.SequenceEqual(after.AddedSavvy) &&
            before.BaseGrowth.SequenceEqual(after.BaseGrowth) &&
            before.GrowthAcceleration.SequenceEqual(
                after.GrowthAcceleration) &&
            before.StatRevisions.SequenceEqual(after.StatRevisions),
            $"{phase} leaves pet and inventory value unchanged");
    }

    private static void AssertPetRebirthStats(
        PetRebirthState before,
        PetRebirthState after,
        PetRebirthGrowthEvidence? growthEvidence,
        decimal minimum = 0.10m,
        decimal maximum = 0.20m)
    {
        if (growthEvidence is not { IsValid: true })
        {
            throw new InvalidDataException(
                "Committed rebirth receipt is missing exact Growth-roll evidence.");
        }
        var receiptIncrease = growthEvidence.ToOrderedIncrease();
        Check.True(
            after.AddedSavvy.Length == 6 &&
            after.BaseGrowth.Length == 6 &&
            after.GrowthAcceleration.Length == 6 &&
            after.StatRevisions.Length == 6,
            "rebirth retains exactly six authoritative stat rows");
        for (var index = 0; index < 6; index++)
        {
            var increase = after.GrowthAcceleration[index] -
                before.GrowthAcceleration[index];
            Check.True(
                after.AddedSavvy[index] ==
                    (after.BaseGrowth[index] +
                     after.GrowthAcceleration[index]) * after.Level &&
                increase == receiptIncrease[index] &&
                increase >= minimum && increase <= maximum &&
                increase * 100m == decimal.Truncate(increase * 100m) &&
                after.StatRevisions[index] ==
                    before.StatRevisions[index] + 1,
                $"rebirth stat {index + 1} resets and rolls within the authored tier");
        }
    }

    private static void AssertPetRebirthStateEqual(
        PetRebirthState expected,
        PetRebirthState actual,
        string phase)
    {
        Check.True(
            expected with
            {
                AddedSavvy = actual.AddedSavvy,
                BaseGrowth = actual.BaseGrowth,
                GrowthAcceleration = actual.GrowthAcceleration,
                StatRevisions = actual.StatRevisions
            } == actual &&
            expected.AddedSavvy.SequenceEqual(actual.AddedSavvy) &&
            expected.BaseGrowth.SequenceEqual(actual.BaseGrowth) &&
            expected.GrowthAcceleration.SequenceEqual(
                actual.GrowthAcceleration) &&
            expected.StatRevisions.SequenceEqual(actual.StatRevisions),
            $"{phase} leaves all authoritative state unchanged");
    }

}
