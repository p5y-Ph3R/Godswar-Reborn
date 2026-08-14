using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const short PhoenixGrowthFeatherSlot = 84;

    private static async Task AssertPetGrowthResetAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation rawCorrelation,
        long petId)
    {
        await SeedPhoenixGrowthResetAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var accelerationBefore = (await ReadGrowthAccelerationAsync(
            dataSource,
            petId)).ToArray();
        var before = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            !before.GrowthRevealed &&
            before.CompletedRebirths == 5 &&
            before.TotalGrowth is >= 0.01m and <= 0.10m,
            "Phoenix fixture starts from exactly five completed Rebirths with effective Growth inside the Weak bracket");

        var firstIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var firstEnvelope = PlayerOwnershipTestFences.Bind(
            PetGrowthResetCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new PetGrowthResetCommand(firstIdentity)));
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(firstEnvelope),
            executor.ExecuteAsync(firstEnvelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetGrowthPreviewed,
            "concurrent Phoenix Growth preview");
        var firstReceipt = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var firstPreview = firstReceipt.GrowthPreview ??
            throw new InvalidDataException(
                "Phoenix Growth preview receipt is missing rates.");
        var afterFirstPreview = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        await AssertPhoenixPreviewAuditAsync(
            dataSource,
            firstIdentity.OperationId,
            firstPreview);
        Check.True(
            firstReceipt.KitBagSlot == PhoenixGrowthFeatherSlot &&
            firstReceipt.PetId == petId &&
            firstPreview.HasAuthoritativeCurrentRates &&
            firstPreview.ToOrderedCurrentRates().Sum() ==
                before.TotalGrowth &&
            firstPreview.UsesRebirthCountWidenedRates &&
            firstPreview.CompletedRebirths == 5 &&
            firstPreview.ToOrderedRebirthModifiers().All(
                static value => value is >= .50m and <= 1.00m) &&
            accelerationBefore.SequenceEqual(
                new decimal[]
                {
                    0.71m, 0.88m, 0.73m, 0.61m, 0.75m, 0.88m
                }) &&
            firstPreview.ToOrderedRates().Sum() is >= 2m and <= 4m &&
            SamePetGrowthValues(afterFirstPreview, before) &&
            afterFirstPreview.FeatherStack == 3 &&
            afterFirstPreview.InventoryRevision ==
                before.InventoryRevision + 1 &&
            afterFirstPreview.GrowthAuditCount ==
                before.GrowthAuditCount + 1 &&
            afterFirstPreview.PendingPreviewOperationId ==
                firstIdentity.OperationId,
            "Reset consumes once and stores a preview without changing pet stats");

        var replay = await restarted.ExecuteAsync(firstEnvelope);
        var replayed = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            replay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replay.Receipt == firstReceipt &&
            SameGrowthResetState(replayed, afterFirstPreview),
            "duplicate Reset replays without another roll or consumption");

        var secondIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var secondEnvelope = PlayerOwnershipTestFences.Bind(
            PetGrowthResetCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new PetGrowthResetCommand(secondIdentity)));
        var secondResult = await executor.ExecuteAsync(secondEnvelope);
        var secondPreview = secondResult.Receipt?.GrowthPreview ??
            throw new InvalidDataException(
                "Second Phoenix Growth preview is missing rates.");
        var afterSecondPreview = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            secondResult.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            SamePetGrowthValues(afterSecondPreview, before) &&
            afterSecondPreview.FeatherStack == 2 &&
            afterSecondPreview.PendingPreviewOperationId ==
                secondIdentity.OperationId,
            "a deliberate second Reset replaces only the pending preview");

        var staleAccept = await executor.ExecuteAsync(
            CreateGrowthAcceptEnvelope(
                subject,
                rawCorrelation,
                firstIdentity.OperationId));
        var afterStaleAccept = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            staleAccept.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            staleAccept.Receipt?.Status ==
                PetDurableReceiptStatus.PetGrowthPreviewUnavailable &&
            SameGrowthResetState(
                afterStaleAccept,
                afterSecondPreview),
            "stale OK cannot apply or delete the newer preview");

        var acceptEnvelope = CreateGrowthAcceptEnvelope(
            subject,
            rawCorrelation,
            secondIdentity.OperationId);
        var accepted = await executor.ExecuteAsync(acceptEnvelope);
        var afterAccept = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var accelerationAfter = await ReadGrowthAccelerationAsync(
            dataSource,
            petId);
        Check.True(
            accepted.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            accepted.Receipt?.Status ==
                PetDurableReceiptStatus.PetGrowthAccepted &&
            afterAccept.GrowthRevealed &&
            afterAccept.TotalGrowth ==
                secondPreview.ToOrderedRates().Sum() &&
            accelerationAfter.SequenceEqual(
                secondPreview.ToOrderedRebirthModifiers()) &&
            afterAccept.HasExactScaledAdded &&
            afterAccept.TotalBasicSavvy == before.TotalBasicSavvy &&
            afterAccept.PetRevision == before.PetRevision + 1 &&
            afterAccept.InventoryRevision ==
                afterSecondPreview.InventoryRevision &&
            afterAccept.FeatherStack == 2 &&
            afterAccept.PendingPreviewOperationId is null &&
            afterAccept.StatRevisions.Zip(
                before.StatRevisions,
                static (current, previous) => current == previous + 1)
                .All(static value => value) &&
            accelerationAfter.All(
                static value => value is >= .50m and <= 1.00m),
            "OK replaces nature Growth and redraws the count-based Rebirth modifier exactly once");

        var acceptedReplay = await restarted.ExecuteAsync(acceptEnvelope);
        var afterAcceptReplay = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            acceptedReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            acceptedReplay.Receipt == accepted.Receipt &&
            SameGrowthResetState(afterAcceptReplay, afterAccept),
            "duplicate OK replays without applying Growth twice");

        var afterLifecycle = await AssertPetGrowthPreviewLifecycleAsync(
            dataSource,
            executor,
            subject,
            rawCorrelation,
            petId,
            afterAccept);

        var missingIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var missing = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetGrowthResetCommandEnvelope.CreateRawLocal(
                    subject,
                    rawCorrelation,
                    DateTimeOffset.UtcNow,
                    new PetGrowthResetCommand(missingIdentity))));
        var unchanged = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            missing.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            missing.Receipt?.Status ==
                PetDurableReceiptStatus.PhoenixFeatherNotFound &&
            unchanged.FeatherStack == 0 &&
            SameGrowthResetState(
                unchanged,
                afterLifecycle,
                compareFeatherStack: false),
            "missing Phoenix Feather cannot mutate or reroll Growth");
    }

    private static CommandEnvelope<PetGrowthResetCommand>
        CreateGrowthAcceptEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation rawCorrelation,
            Guid previewOperationId)
    {
        var identity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        return PlayerOwnershipTestFences.Bind(
            PetGrowthResetCommandEnvelope.CreateRawLocal(
                subject,
                rawCorrelation,
                DateTimeOffset.UtcNow,
                new PetGrowthResetCommand(
                    identity,
                    PetGrowthResetOperation.Accept,
                    previewOperationId)));
    }

    private static bool SamePetGrowthValues(
        PetGrowthResetState left,
        PetGrowthResetState right) =>
        left.GrowthRevealed == right.GrowthRevealed &&
        left.PetRevision == right.PetRevision &&
        left.TotalBasicSavvy == right.TotalBasicSavvy &&
        left.TotalGrowth == right.TotalGrowth &&
        left.TotalAddedSavvy == right.TotalAddedSavvy &&
        left.HasExactScaledAdded == right.HasExactScaledAdded &&
        left.CompletedRebirths == right.CompletedRebirths &&
        left.StatRevisions.SequenceEqual(right.StatRevisions);

    private static bool SameGrowthResetState(
        PetGrowthResetState left,
        PetGrowthResetState right,
        bool compareFeatherStack = true) =>
        left.GrowthRevealed == right.GrowthRevealed &&
        left.PetRevision == right.PetRevision &&
        left.TotalBasicSavvy == right.TotalBasicSavvy &&
        left.TotalGrowth == right.TotalGrowth &&
        left.TotalAddedSavvy == right.TotalAddedSavvy &&
        left.HasExactScaledAdded == right.HasExactScaledAdded &&
        left.CompletedRebirths == right.CompletedRebirths &&
        left.StatRevisions.SequenceEqual(right.StatRevisions) &&
        left.InventoryRevision == right.InventoryRevision &&
        (!compareFeatherStack ||
         left.FeatherStack == right.FeatherStack) &&
        left.GrowthAuditCount == right.GrowthAuditCount &&
        left.PendingPreviewOperationId ==
            right.PendingPreviewOperationId;

    private static async Task SeedPhoenixGrowthResetAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = true,
                is_summoned = true,
                completed_rebirths = 5
            WHERE id = @petId
              AND user_id = @characterId;

            UPDATE public.character_pet_stat_values
            SET growth_acceleration = CASE stat_code
                    WHEN 1 THEN 0.71
                    WHEN 2 THEN 0.88
                    WHEN 3 THEN 0.73
                    WHEN 4 THEN 0.61
                    WHEN 5 THEN 0.75
                    WHEN 6 THEN 0.88
                END,
                added_savvy = (
                    base_growth_rate + CASE stat_code
                        WHEN 1 THEN 0.71
                        WHEN 2 THEN 0.88
                        WHEN 3 THEN 0.73
                        WHEN 4 THEN 0.61
                        WHEN 5 THEN 0.75
                        WHEN 6 THEN 0.88
                    END
                ) * (SELECT level FROM public.character_pets
                    WHERE id = @petId),
                revision = revision + 1
            WHERE pet_id = @petId;

            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @featherSlot, 11005,
                1, 1, 1, 4, 0, 0
            );
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "featherSlot",
            PhoenixGrowthFeatherSlot);
        Check.Equal(
            8,
            await command.ExecuteNonQueryAsync(),
            "Phoenix fixture seeds a five-rebirth pet and one feather stack");
    }

    private static async Task<decimal[]> ReadGrowthAccelerationAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT growth_acceleration
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code;
            """);
        command.Parameters.AddWithValue("petId", petId);
        var values = new List<decimal>(6);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetDecimal(0));
        }
        return values.ToArray();
    }

    private static async Task DeletePhoenixFeatherAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = 11005;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Phoenix Growth fixture removes its remaining feather");
    }

    private static async Task<PetGrowthResetState>
        ReadPetGrowthResetStateAsync(
            NpgsqlDataSource dataSource,
            int characterId,
            long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.growth_revealed,
                pet.revision,
                sum(stat.initial_savvy),
                sum(stat.base_growth_rate),
                sum(stat.added_savvy),
                bool_and(
                    stat.added_savvy =
                        (
                            stat.base_growth_rate +
                            stat.growth_acceleration
                        ) * pet.level
                ),
                ARRAY(
                    SELECT revision
                    FROM public.character_pet_stat_values ordered
                    WHERE ordered.pet_id = pet.id
                    ORDER BY stat_code
                ),
                base.inventory_revision,
                coalesce((
                    SELECT sum(stack)
                    FROM public.character_items
                    WHERE user_id = @characterId
                      AND item_location = 1
                      AND prop_id = 11005
                ), 0),
                (
                    SELECT count(*)
                    FROM public.pet_operation_audit
                    WHERE user_id = @characterId
                      AND pet_id = @petId
                      AND operation = 'reveal_growth'
                      AND outcome = 'committed'
                ),
                (
                    SELECT preview_operation_id
                    FROM public.character_pet_growth_previews
                    WHERE user_id = @characterId
                ),
                pet.completed_rebirths
            FROM public.character_pets pet
            JOIN public.character_pet_stat_values stat
              ON stat.pet_id = pet.id
            JOIN public.character_base base
              ON base.id = pet.user_id
            WHERE pet.id = @petId
              AND pet.user_id = @characterId
            GROUP BY pet.id, base.inventory_revision;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Phoenix Growth state was not returned.");
        }
        return new PetGrowthResetState(
            reader.GetBoolean(0),
            reader.GetInt64(1),
            reader.GetDecimal(2),
            reader.GetDecimal(3),
            reader.GetDecimal(4),
            reader.GetBoolean(5),
            reader.GetFieldValue<long[]>(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.GetInt16(11));
    }

    private sealed record PetGrowthResetState(
        bool GrowthRevealed,
        long PetRevision,
        decimal TotalBasicSavvy,
        decimal TotalGrowth,
        decimal TotalAddedSavvy,
        bool HasExactScaledAdded,
        long[] StatRevisions,
        long InventoryRevision,
        long FeatherStack,
        long GrowthAuditCount,
        Guid? PendingPreviewOperationId,
        short CompletedRebirths);
}
