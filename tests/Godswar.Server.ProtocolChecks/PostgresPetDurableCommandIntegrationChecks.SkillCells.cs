using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const short PetEnhanceSpringSlot = 86;
    private const short GoldenAppleJuiceSlot = 87;
    private const short SkillItemFixtureStack = 3;

    private static async Task AssertPetSkillCellItemsAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        long petId)
    {
        await SeedPetSkillCellItemsAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var initial = await ReadPetSkillCellItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            initial is
            {
                LearnedSkillCount: 1,
                OpenedSkillSlots: 1,
                AvailableSkillSlots: 1,
                IsCarried: true,
                SpringStack: SkillItemFixtureStack,
                AppleStack: SkillItemFixtureStack
            },
            "pet skill-cell fixture starts from one learned/open/available cell");

        var prematureApple = CreatePetSkillCellItemEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            GoldenAppleJuiceSlot);
        var rejectedApple = await executor.ExecuteAsync(prematureApple);
        var replayedRejection = await restarted.ExecuteAsync(
            prematureApple);
        Check.True(
            rejectedApple.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            rejectedApple.Receipt?.Status ==
                PetDurableReceiptStatus.PetSkillCellNotAvailable &&
            replayedRejection.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replayedRejection.Receipt == rejectedApple.Receipt,
            "Golden Apple Juice without a sealed cell is durably rejected and replay-safe");
        var afterRejectedApple = await ReadPetSkillCellItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        AssertPetSkillValueStateUnchanged(
            initial,
            afterRejectedApple,
            "rejected Golden Apple Juice");

        var springEnvelope = CreatePetSkillCellItemEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            PetEnhanceSpringSlot);
        var concurrentSpring = await Task.WhenAll(
            executor.ExecuteAsync(springEnvelope),
            executor.ExecuteAsync(springEnvelope));
        AssertCommitAndDuplicate(
            concurrentSpring,
            PetDurableReceiptStatus.PetSkillCellMadeAvailable,
            "concurrent Pet Enhance Spring");
        var springReceipt = concurrentSpring.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var afterSpring = await ReadPetSkillCellItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            springReceipt.PetId == petId &&
            springReceipt.PetRevision == afterSpring.PetRevision &&
            afterSpring.LearnedSkillCount == 1 &&
            afterSpring.OpenedSkillSlots == 1 &&
            afterSpring.AvailableSkillSlots == 2 &&
            afterSpring.IsCarried &&
            afterSpring.SpringStack == SkillItemFixtureStack - 1 &&
            afterSpring.AppleStack == SkillItemFixtureStack &&
            afterSpring.PetRevision == initial.PetRevision + 1 &&
            afterSpring.InventoryRevision ==
                initial.InventoryRevision + 1 &&
            afterSpring.InventoryLedgerCount ==
                initial.InventoryLedgerCount + 1 &&
            afterSpring.InventoryOutboxCount ==
                initial.InventoryOutboxCount + 1,
            "Pet Enhance Spring atomically opens availability and consumes exactly one stack unit");

        var appleEnvelope = CreatePetSkillCellItemEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            GoldenAppleJuiceSlot);
        var concurrentApple = await Task.WhenAll(
            executor.ExecuteAsync(appleEnvelope),
            executor.ExecuteAsync(appleEnvelope));
        AssertCommitAndDuplicate(
            concurrentApple,
            PetDurableReceiptStatus.PetSkillCellOpened,
            "concurrent Golden Apple Juice");
        var appleReceipt = concurrentApple.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var afterApple = await ReadPetSkillCellItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            appleReceipt.PetId == petId &&
            appleReceipt.PetRevision == afterApple.PetRevision &&
            afterApple.LearnedSkillCount == 1 &&
            afterApple.OpenedSkillSlots == 2 &&
            afterApple.AvailableSkillSlots == 2 &&
            afterApple.IsCarried &&
            afterApple.SpringStack == SkillItemFixtureStack - 1 &&
            afterApple.AppleStack == SkillItemFixtureStack - 1 &&
            afterApple.PetRevision == afterSpring.PetRevision + 1 &&
            afterApple.InventoryRevision ==
                afterSpring.InventoryRevision + 1 &&
            afterApple.InventoryLedgerCount ==
                afterSpring.InventoryLedgerCount + 1 &&
            afterApple.InventoryOutboxCount ==
                afterSpring.InventoryOutboxCount + 1,
            "Golden Apple Juice atomically opens the available cell and consumes exactly one stack unit");

        await SetPetSkillCellStateAsync(
            dataSource,
            petId,
            openedSkillSlots:
                PetSkillSlotPolicy.MaximumLearnableSkillCells,
            availableSkillSlots:
                PetSkillSlotPolicy.MaximumLearnableSkillCells,
            isCarried: true);
        var maximumBefore = await ReadPetSkillCellItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var maximumEnvelope = CreatePetSkillCellItemEnvelope(
            subject,
            correlation,
            Guid.NewGuid(),
            PetEnhanceSpringSlot);
        var maximum = await executor.ExecuteAsync(maximumEnvelope);
        var maximumReplay = await restarted.ExecuteAsync(maximumEnvelope);
        Check.True(
            maximum.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            maximum.Receipt?.Status ==
                PetDurableReceiptStatus.PetSkillCellMaximumReached &&
            maximumReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            maximumReplay.Receipt == maximum.Receipt,
            "twelfth skill-cell boundary rejects another spring durably");
        AssertPetSkillValueStateUnchanged(
            maximumBefore,
            await ReadPetSkillCellItemStateAsync(
                dataSource,
                subject.CharacterId,
                petId),
            "maximum-cell Pet Enhance Spring");

        await SetPetSkillCellStateAsync(
            dataSource,
            petId,
            openedSkillSlots: 1,
            availableSkillSlots: 2,
            isCarried: false);
        var notCarriedBefore = await ReadPetSkillCellItemStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var notCarried = await executor.ExecuteAsync(
            CreatePetSkillCellItemEnvelope(
                subject,
                correlation,
                Guid.NewGuid(),
                GoldenAppleJuiceSlot));
        Check.True(
            notCarried.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            notCarried.Receipt?.Status ==
                PetDurableReceiptStatus.PetNotTaken,
            "skill-cell items require one authoritative carried pet");
        AssertPetSkillValueStateUnchanged(
            notCarriedBefore,
            await ReadPetSkillCellItemStateAsync(
                dataSource,
                subject.CharacterId,
                petId),
            "not-carried skill-cell item");

        // Leave the shared fixture usable by the subsequent presence checks.
        await SetPetSkillCellStateAsync(
            dataSource,
            petId,
            openedSkillSlots: 2,
            availableSkillSlots: 2,
            isCarried: true);
    }

    private static CommandEnvelope<BagItemActivationCommand>
        CreatePetSkillCellItemEnvelope(
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

    private static async Task SeedPetSkillCellItemsAsync(
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
            SET opened_skill_slots = 1,
                available_skill_slots = 1,
                is_carried = true,
                activity_state = 'owned'
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
                "pet skill-cell fixture prepares the carried pet");
        }
        await using (var items = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES
                (
                    @characterId, 1, @springSlot, @springItemId,
                    0, 1, 1, @stack, 0, 0
                ),
                (
                    @characterId, 1, @appleSlot, @appleItemId,
                    0, 1, 1, @stack, 0, 0
                );
            """,
            connection,
            transaction))
        {
            items.Parameters.AddWithValue("characterId", characterId);
            items.Parameters.AddWithValue(
                "springSlot",
                PetEnhanceSpringSlot);
            items.Parameters.AddWithValue(
                "springItemId",
                checked((int)PetItemCatalog.PetEnhanceSpring));
            items.Parameters.AddWithValue(
                "appleSlot",
                GoldenAppleJuiceSlot);
            items.Parameters.AddWithValue(
                "appleItemId",
                checked((int)PetItemCatalog.GoldenAppleJuice));
            items.Parameters.AddWithValue(
                "stack",
                SkillItemFixtureStack);
            Check.Equal(
                2,
                await items.ExecuteNonQueryAsync(),
                "pet skill-cell fixture inserts both stock items");
        }
        await transaction.CommitAsync();
    }

    private static async Task SetPetSkillCellStateAsync(
        NpgsqlDataSource dataSource,
        long petId,
        short openedSkillSlots,
        short availableSkillSlots,
        bool isCarried)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET opened_skill_slots = @openedSkillSlots,
                available_skill_slots = @availableSkillSlots,
                is_carried = @isCarried,
                is_summoned = false,
                contributes_to_character = false
            WHERE id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "openedSkillSlots",
            openedSkillSlots);
        command.Parameters.AddWithValue(
            "availableSkillSlots",
            availableSkillSlots);
        command.Parameters.AddWithValue("isCarried", isCarried);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet skill-cell fixture updates one pet state");
    }

    private static async Task<PetSkillCellItemState>
        ReadPetSkillCellItemStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (
                    SELECT count(*)
                    FROM public.character_pet_skills
                    WHERE pet_id = pet.id
                ),
                pet.opened_skill_slots,
                pet.available_skill_slots,
                pet.revision,
                pet.is_carried,
                COALESCE((
                    SELECT stack
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND slot_index = @springSlot
                      AND prop_id = @springItemId
                ), 0),
                COALESCE((
                    SELECT stack
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND slot_index = @appleSlot
                      AND prop_id = @appleItemId
                ), 0),
                character_row.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger
                    WHERE character_id = @characterId
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events
                    WHERE consumer_key = 'inventory_projection_v1'
                      AND aggregate_type = 'character_inventory'
                      AND aggregate_key = concat(
                          'character:', @characterId, ':inventory')
                      AND event_type =
                          'inventory.pet_bag_item_activated'
                )
            FROM public.character_pets pet
            JOIN public.character_base character_row
              ON character_row.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "springSlot",
            PetEnhanceSpringSlot);
        command.Parameters.AddWithValue(
            "springItemId",
            checked((int)PetItemCatalog.PetEnhanceSpring));
        command.Parameters.AddWithValue(
            "appleSlot",
            GoldenAppleJuiceSlot);
        command.Parameters.AddWithValue(
            "appleItemId",
            checked((int)PetItemCatalog.GoldenAppleJuice));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet skill-cell item state disappeared.");
        }
        return new PetSkillCellItemState(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt64(3),
            reader.GetBoolean(4),
            reader.GetInt16(5),
            reader.GetInt16(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9));
    }

    private static void AssertPetSkillValueStateUnchanged(
        PetSkillCellItemState before,
        PetSkillCellItemState after,
        string scope)
    {
        Check.True(
            after.LearnedSkillCount == before.LearnedSkillCount &&
            after.OpenedSkillSlots == before.OpenedSkillSlots &&
            after.AvailableSkillSlots == before.AvailableSkillSlots &&
            after.PetRevision == before.PetRevision &&
            after.IsCarried == before.IsCarried &&
            after.SpringStack == before.SpringStack &&
            after.AppleStack == before.AppleStack &&
            after.InventoryRevision == before.InventoryRevision &&
            after.InventoryLedgerCount == before.InventoryLedgerCount &&
            after.InventoryOutboxCount == before.InventoryOutboxCount,
            $"{scope} preserves pet and inventory value state");
    }

    private sealed record PetSkillCellItemState(
        long LearnedSkillCount,
        short OpenedSkillSlots,
        short AvailableSkillSlots,
        long PetRevision,
        bool IsCarried,
        short SpringStack,
        short AppleStack,
        long InventoryRevision,
        long InventoryLedgerCount,
        long InventoryOutboxCount);
}
