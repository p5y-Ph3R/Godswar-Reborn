using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const short MorningDewSlot = 82;
    private const short RestrictedMorningDewSlot = 83;

    private static async Task AssertPetExperienceItemsAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long petId)
    {
        await SeedPetExperienceItemsAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var initial = await ReadPetExperienceItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var initialEvidence = await ReadPetExperienceEvidenceAsync(
            dataSource,
            subject.CharacterId);

        var normalEnvelope = CreatePetExperienceItemEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            MorningDewSlot);
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(normalEnvelope),
            executor.ExecuteAsync(normalEnvelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetExperienceAdded,
            "concurrent Morning Dew 5");
        var normalReceipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var afterNormal = await ReadPetExperienceItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var afterNormalEvidence = await ReadPetExperienceEvidenceAsync(
            dataSource,
            subject.CharacterId);
        Check.True(
            normalReceipt.PetExperience == 10_001_000 &&
            normalReceipt.PetRevision == afterNormal.PetRevision &&
            afterNormal.Experience == 10_001_000 &&
            afterNormal.NormalStack == 1 &&
            afterNormal.RestrictedStack == 2 &&
            afterNormal.PetRevision == initial.PetRevision + 1 &&
            afterNormal.InventoryRevision == initial.InventoryRevision + 1 &&
            afterNormal.InventoryLedgerCount ==
                initial.InventoryLedgerCount + 1 &&
            afterNormal.InventoryOutboxCount ==
                initial.InventoryOutboxCount + 1 &&
            afterNormalEvidence.AuditCount ==
                initialEvidence.AuditCount + 1 &&
            afterNormalEvidence.InboxCount ==
                initialEvidence.InboxCount + 1 &&
            afterNormalEvidence.OutboxCount ==
                initialEvidence.OutboxCount + 1,
            "Morning Dew atomically adds EXP, advances both revisions, and consumes one item");
        var replayed = await restarted.ExecuteAsync(normalEnvelope);
        Check.True(
            replayed.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replayed.Receipt == normalReceipt &&
            await ReadPetExperienceItemStateAsync(
                dataSource,
                subject.CharacterId,
                petId) == afterNormal,
            "Morning Dew replay cannot duplicate pet EXP or item consumption");

        await AssertImmediateConsumableCooldownAsync(
            dataSource,
            executor,
            restarted,
            subject,
            correlation,
            petId,
            afterNormal);

        var restrictedEnvelope = CreatePetExperienceItemEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            RestrictedMorningDewSlot);
        var rejected = await executor.ExecuteAsync(restrictedEnvelope);
        var rejectedReplay = await restarted.ExecuteAsync(
            restrictedEnvelope);
        Check.True(
            rejected.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            rejected.Receipt?.Status ==
                PetDurableReceiptStatus
                    .PetExperienceRestrictedPetUnbound &&
            rejectedReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            rejectedReplay.Receipt == rejected.Receipt &&
            await ReadPetExperienceItemStateAsync(
                dataSource,
                subject.CharacterId,
                petId) == afterNormal,
            "restricted Morning Dew durably rejects an unbound carried pet without mutation");

        await SetPetBoundAsync(dataSource, petId, isBound: true);
        var beforeRestricted = await ReadPetExperienceItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var restrictedCommitted = await executor.ExecuteAsync(
            CreatePetExperienceItemEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                RestrictedMorningDewSlot));
        var afterRestricted = await ReadPetExperienceItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            restrictedCommitted is
            {
                Disposition: PetDurableExecutionDisposition.Committed,
                Receipt.Status:
                    PetDurableReceiptStatus.PetExperienceAdded
            } &&
            afterRestricted.Experience == 20_001_000 &&
            afterRestricted.RestrictedStack == 1 &&
            afterRestricted.PetRevision == beforeRestricted.PetRevision + 1 &&
            afterRestricted.InventoryRevision ==
                beforeRestricted.InventoryRevision + 1,
            "restricted Morning Dew adds EXP only after the carried pet is bound");

        await ExpireConsumableCooldownAsync(
            dataSource,
            subject.CharacterId,
            4721);

        await SetPetExperienceForBoundaryAsync(
            dataSource,
            petId,
            PetExperienceItemPolicy.MaximumNativePetExperience - 1);
        var beforeMaximum = await ReadPetExperienceItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var maximum = await executor.ExecuteAsync(
            CreatePetExperienceItemEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                MorningDewSlot));
        Check.True(
            maximum.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            maximum.Receipt?.Status ==
                PetDurableReceiptStatus.PetExperienceMaximumReached &&
            await ReadPetExperienceItemStateAsync(
                dataSource,
                subject.CharacterId,
                petId) == beforeMaximum,
            "Morning Dew cannot overflow the native unsigned pet EXP field");
    }

    private static CommandEnvelope<BagItemActivationCommand>
        CreatePetExperienceItemEnvelope(
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        Guid operationId,
        short kitBagSlot) =>
        PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        operationId,
                        correlation.ConnectionId),
                    kitBagSlot)));

    private static async Task SeedPetExperienceItemsAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var pet = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET experience = 1000,
                bound = false,
                activity_state = 'owned',
                is_carried = true,
                is_summoned = false,
                contributes_to_character = false
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
                "Morning Dew fixture prepares one carried pet");
        }
        await using (var items = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES
                (@characterId, 1, @normalSlot, @normalItem,
                 0, 1, 0, 2, 0, 0),
                (@characterId, 1, @restrictedSlot, @restrictedItem,
                 0, 1, 1, 2, 0, 0);
            """,
            connection,
            transaction))
        {
            items.Parameters.AddWithValue("characterId", characterId);
            items.Parameters.AddWithValue("normalSlot", MorningDewSlot);
            items.Parameters.AddWithValue(
                "normalItem",
                checked((int)PetExperienceItemPolicy.LastMorningDew));
            items.Parameters.AddWithValue(
                "restrictedSlot",
                RestrictedMorningDewSlot);
            items.Parameters.AddWithValue(
                "restrictedItem",
                checked((int)PetExperienceItemPolicy
                    .LastRestrictedMorningDew));
            Check.Equal(
                2,
                await items.ExecuteNonQueryAsync(),
                "Morning Dew fixture inserts normal and restricted stacks");
        }
        await transaction.CommitAsync();
    }

    private static async Task SetPetBoundAsync(
        NpgsqlDataSource dataSource,
        long petId,
        bool isBound)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET bound = @isBound,
                revision = revision + 1
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("isBound", isBound);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Morning Dew fixture updates pet binding");
    }

    private static async Task SetPetExperienceForBoundaryAsync(
        NpgsqlDataSource dataSource,
        long petId,
        long experience)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET experience = @experience,
                revision = revision + 1
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("experience", experience);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Morning Dew fixture sets the native EXP boundary");
    }

    private static async Task<PetExperienceItemState>
        ReadPetExperienceItemStateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.experience,
                pet.revision,
                pet.bound,
                COALESCE((
                    SELECT stack FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND slot_index = @normalSlot
                ), 0),
                COALESCE((
                    SELECT stack FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND slot_index = @restrictedSlot
                ), 0),
                character_row.inventory_revision,
                (SELECT count(*) FROM public.character_inventory_ledger
                 WHERE character_id = @characterId),
                (SELECT count(*) FROM public.outbox_events
                 WHERE consumer_key = 'inventory_projection_v1'
                   AND aggregate_type = 'character_inventory'
                   AND aggregate_key = @inventoryAggregate)
            FROM public.character_pets pet
            JOIN public.character_base character_row
              ON character_row.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("normalSlot", MorningDewSlot);
        command.Parameters.AddWithValue(
            "restrictedSlot",
            RestrictedMorningDewSlot);
        command.Parameters.AddWithValue(
            "inventoryAggregate",
            $"character:{characterId}:inventory");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Morning Dew fixture state disappeared.");
        }
        return new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetInt16(3),
            reader.GetInt16(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    private static async Task<PetExperienceEvidence>
        ReadPetExperienceEvidenceAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM public.command_audit
                 WHERE aggregate_type = 'character_pet_value'
                   AND aggregate_key = @aggregateKey),
                (SELECT count(*) FROM public.command_inbox
                 WHERE aggregate_type = 'character_pet_value'
                   AND aggregate_key = @aggregateKey),
                (SELECT count(*) FROM public.outbox_events
                 WHERE aggregate_type = 'character_pet_value'
                   AND aggregate_key = @aggregateKey)
            """);
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"character:{characterId}");
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "Morning Dew durable evidence query returns one row");
        return new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private sealed record PetExperienceItemState(
        long Experience,
        long PetRevision,
        bool IsBound,
        short NormalStack,
        short RestrictedStack,
        long InventoryRevision,
        long InventoryLedgerCount,
        long InventoryOutboxCount);

    private sealed record PetExperienceEvidence(
        long AuditCount,
        long InboxCount,
        long OutboxCount);
}
