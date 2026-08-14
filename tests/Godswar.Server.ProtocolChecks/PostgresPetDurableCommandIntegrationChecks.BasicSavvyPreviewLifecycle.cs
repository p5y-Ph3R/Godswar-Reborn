using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task<PetBasicSavvyResetState>
        AssertPetBasicSavvyPreviewLifecycleAsync(
            NpgsqlDataSource dataSource,
            PostgresPetDurableCommandExecutor executor,
            CommandSubject subject,
            CommandConnectionCorrelation rawCorrelation,
            long petId,
            PetBasicSavvyResetState baseline)
    {
        var ownership = PlayerOwnershipTestFences.ForCharacter(
            subject.CharacterId);
        var basicLifecycle =
            (IPetBasicSavvyPreviewLifecycleStore)executor;
        var growthLifecycle = (IPetGrowthPreviewLifecycleStore)executor;

        var discardPreview = await CreateBasicSavvyPreviewAsync(
            executor,
            subject,
            rawCorrelation);
        Check.True(
            await basicLifecycle.IsCurrentAsync(
                subject,
                ownership,
                rawCorrelation.ConnectionId,
                discardPreview.PreviewOperationId) &&
            !await growthLifecycle.IsCurrentAsync(
                subject,
                ownership,
                rawCorrelation.ConnectionId,
                discardPreview.PreviewOperationId),
            "Basic lifecycle reads only the Fairy preview table");
        await basicLifecycle.DiscardForSessionAsync(
            subject,
            ownership,
            rawCorrelation.ConnectionId);
        await basicLifecycle.DiscardForSessionAsync(
            subject,
            ownership,
            rawCorrelation.ConnectionId);
        var afterDiscard = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            SamePetBasicSavvyValues(afterDiscard, baseline) &&
            afterDiscard.PendingPreviewOperationId is null &&
            afterDiscard.FeatherStack == baseline.FeatherStack - 1 &&
            afterDiscard.InventoryRevision ==
                baseline.InventoryRevision + 1 &&
            afterDiscard.BasicSavvyAuditCount ==
                baseline.BasicSavvyAuditCount + 1,
            "session discard is idempotent and retains the paid original Basic state");

        var switchedPetPreview = await CreateBasicSavvyPreviewAsync(
            executor,
            subject,
            rawCorrelation);
        var alternatePetId = await SeedPresenceSwitchPetAsync(
            dataSource,
            subject.CharacterId,
            petId);
        await SwitchSummonedPetAsync(
            dataSource,
            subject.CharacterId,
            petId,
            alternatePetId);
        var primaryBeforeSwitchedAccept =
            await ReadPetBasicSavvyResetStateAsync(
                dataSource,
                subject.CharacterId,
                petId);
        var alternateBeforeSwitchedAccept =
            await ReadPetBasicSavvyResetStateAsync(
                dataSource,
                subject.CharacterId,
                alternatePetId);
        var switchedAccept = await executor.ExecuteAsync(
            CreateBasicSavvyAcceptEnvelope(
                subject,
                rawCorrelation,
                switchedPetPreview.PreviewOperationId));
        var primaryAfterSwitchedAccept =
            await ReadPetBasicSavvyResetStateAsync(
                dataSource,
                subject.CharacterId,
                petId);
        var alternateAfterSwitchedAccept =
            await ReadPetBasicSavvyResetStateAsync(
                dataSource,
                subject.CharacterId,
                alternatePetId);
        Check.True(
            switchedAccept.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            switchedAccept.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable &&
            primaryAfterSwitchedAccept.PendingPreviewOperationId is null &&
            alternateAfterSwitchedAccept.PendingPreviewOperationId is null &&
            SamePetBasicSavvyValues(
                primaryAfterSwitchedAccept,
                primaryBeforeSwitchedAccept) &&
            SamePetBasicSavvyValues(
                alternateAfterSwitchedAccept,
                alternateBeforeSwitchedAccept) &&
            primaryAfterSwitchedAccept.FeatherStack ==
                primaryBeforeSwitchedAccept.FeatherStack &&
            primaryAfterSwitchedAccept.InventoryRevision ==
                primaryBeforeSwitchedAccept.InventoryRevision &&
            primaryAfterSwitchedAccept.BasicSavvyAuditCount ==
                primaryBeforeSwitchedAccept.BasicSavvyAuditCount,
            "switching the summoned pet invalidates and removes the paid preview without another consumption or either pet mutation");
        await RestorePrimaryPetAndDeleteAlternateAsync(
            dataSource,
            subject.CharacterId,
            petId,
            alternatePetId);
        baseline = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);

        var staleRevisionPreview = await CreateBasicSavvyPreviewAsync(
            executor,
            subject,
            rawCorrelation);
        await AdvanceOneBasicSavvyStatRevisionAsync(
            dataSource,
            petId);
        var afterExternalRevision = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var staleRevisionAccept = await executor.ExecuteAsync(
            CreateBasicSavvyAcceptEnvelope(
                subject,
                rawCorrelation,
                staleRevisionPreview.PreviewOperationId));
        var afterStaleRevision = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            staleRevisionAccept.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            staleRevisionAccept.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable &&
            afterStaleRevision.PendingPreviewOperationId is null &&
            afterStaleRevision.BasicValues.SequenceEqual(
                afterExternalRevision.BasicValues) &&
            afterStaleRevision.BirthValues.SequenceEqual(
                afterExternalRevision.BirthValues) &&
            afterStaleRevision.RarityValues.SequenceEqual(
                afterExternalRevision.RarityValues) &&
            afterStaleRevision.StatRevisions.SequenceEqual(
                afterExternalRevision.StatRevisions) &&
            afterStaleRevision.PetRevision ==
                afterExternalRevision.PetRevision &&
            afterStaleRevision.FeatherStack ==
                afterExternalRevision.FeatherStack,
            "a changed stat revision invalidates and removes the preview without a partial update");

        var expiringPreview = await CreateBasicSavvyPreviewAsync(
            executor,
            subject,
            rawCorrelation);
        await ExpireBasicSavvyPreviewAsync(
            dataSource,
            subject.CharacterId,
            expiringPreview.PreviewOperationId);
        Check.True(
            !await basicLifecycle.IsCurrentAsync(
                subject,
                ownership,
                rawCorrelation.ConnectionId,
                expiringPreview.PreviewOperationId),
            "expired Fairy preview is not current");
        var beforeExpiredAccept = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        var expiredAccept = await executor.ExecuteAsync(
            CreateBasicSavvyAcceptEnvelope(
                subject,
                rawCorrelation,
                expiringPreview.PreviewOperationId));
        var afterExpiredAccept = await ReadPetBasicSavvyResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            expiredAccept.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            expiredAccept.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewUnavailable &&
            afterExpiredAccept.PendingPreviewOperationId is null &&
            afterExpiredAccept.BasicValues.SequenceEqual(
                beforeExpiredAccept.BasicValues) &&
            afterExpiredAccept.BirthValues.SequenceEqual(
                beforeExpiredAccept.BirthValues) &&
            afterExpiredAccept.RarityValues.SequenceEqual(
                beforeExpiredAccept.RarityValues) &&
            afterExpiredAccept.StatRevisions.SequenceEqual(
                beforeExpiredAccept.StatRevisions) &&
            afterExpiredAccept.PetRevision ==
                beforeExpiredAccept.PetRevision &&
            afterExpiredAccept.FeatherStack ==
                beforeExpiredAccept.FeatherStack,
            "expired Fairy OK deletes only the preview and never mutates Basic");
        return afterExpiredAccept;
    }

    private static async Task<PetBasicSavvyPreviewSnapshot>
        CreateBasicSavvyPreviewAsync(
            PostgresPetDurableCommandExecutor executor,
            CommandSubject subject,
            CommandConnectionCorrelation rawCorrelation)
    {
        var identity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var result = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetBasicSavvyResetCommandEnvelope.CreateRawLocal(
                    subject,
                    rawCorrelation,
                    DateTimeOffset.UtcNow,
                    new PetBasicSavvyResetCommand(identity))));
        Check.True(
            result.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            result.Receipt?.Status ==
                PetDurableReceiptStatus.PetBasicSavvyPreviewed,
            "Fairy lifecycle preview commits");
        return result.Receipt!.BasicSavvyPreview ??
            throw new InvalidDataException(
                "Fairy lifecycle preview is missing its snapshot.");
    }

    private static async Task AdvanceOneBasicSavvyStatRevisionAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pet_stat_values
            SET revision = revision + 1
            WHERE pet_id = @petId
              AND stat_code = 6;
            """);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Fairy CAS fixture advances one stat revision");
    }

    private static async Task ExpireBasicSavvyPreviewAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        Guid previewOperationId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pet_basic_savvy_previews
            SET created_at = clock_timestamp() - interval '2 seconds',
                expires_at = clock_timestamp() - interval '1 second'
            WHERE user_id = @characterId
              AND preview_operation_id = @previewOperationId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "previewOperationId",
            previewOperationId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "Fairy lifecycle fixture expires one preview");
    }

    private static async Task SwitchSummonedPetAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long previousPetId,
        long nextPetId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = id = @nextPetId,
                is_summoned = id = @nextPetId,
                contributes_to_character = false
            WHERE user_id = @characterId
              AND id IN (@previousPetId, @nextPetId);
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("previousPetId", previousPetId);
        command.Parameters.AddWithValue("nextPetId", nextPetId);
        Check.Equal(
            2,
            await command.ExecuteNonQueryAsync(),
            "Fairy lifecycle fixture switches the summoned pet");
    }

    private static async Task RestorePrimaryPetAndDeleteAlternateAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        long primaryPetId,
        long alternatePetId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var delete = new NpgsqlCommand(
            """
            DELETE FROM public.character_pets
            WHERE user_id = @characterId
              AND id = @alternatePetId;
            """,
            connection,
            transaction))
        {
            delete.Parameters.AddWithValue("characterId", characterId);
            delete.Parameters.AddWithValue("alternatePetId", alternatePetId);
            Check.Equal(
                1,
                await delete.ExecuteNonQueryAsync(),
                "Fairy lifecycle fixture removes the alternate pet");
        }
        await using (var restore = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET is_carried = true,
                is_summoned = true,
                contributes_to_character = false
            WHERE user_id = @characterId
              AND id = @primaryPetId;
            """,
            connection,
            transaction))
        {
            restore.Parameters.AddWithValue("characterId", characterId);
            restore.Parameters.AddWithValue("primaryPetId", primaryPetId);
            Check.Equal(
                1,
                await restore.ExecuteNonQueryAsync(),
                "Fairy lifecycle fixture restores the primary pet");
        }
        await transaction.CommitAsync();
    }
}
