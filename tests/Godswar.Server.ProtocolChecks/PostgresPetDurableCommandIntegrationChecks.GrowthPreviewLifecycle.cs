using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task<PetGrowthResetState>
        AssertPetGrowthPreviewLifecycleAsync(
            NpgsqlDataSource dataSource,
            PostgresPetDurableCommandExecutor executor,
            CommandSubject subject,
            CommandConnectionCorrelation rawCorrelation,
            long petId,
            PetGrowthResetState acceptedState)
    {
        var thirdIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var third = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetGrowthResetCommandEnvelope.CreateRawLocal(
                    subject,
                    rawCorrelation,
                    DateTimeOffset.UtcNow,
                    new PetGrowthResetCommand(thirdIdentity))));
        var ownership = PlayerOwnershipTestFences.ForCharacter(
            subject.CharacterId);
        Check.True(
            third.Receipt?.Status ==
                PetDurableReceiptStatus.PetGrowthPreviewed &&
            await executor.IsCurrentAsync(
                subject,
                ownership,
                rawCorrelation.ConnectionId,
                thirdIdentity.OperationId) &&
            !await executor.IsCurrentAsync(
                subject,
                ownership,
                Guid.NewGuid(),
                thirdIdentity.OperationId),
            "a preview is visible only to its owning connection");

        await AdvanceGrowthStatRevisionAsync(dataSource, petId, 1);
        try
        {
            Check.True(
                !await executor.IsCurrentAsync(
                    subject,
                    ownership,
                    rawCorrelation.ConnectionId,
                    thirdIdentity.OperationId),
                "a changed stat revision invalidates the Growth preview page");
        }
        finally
        {
            await RestoreGrowthStatRevisionAsync(dataSource, petId, 1);
        }
        Check.True(
            await executor.IsCurrentAsync(
                subject,
                ownership,
                rawCorrelation.ConnectionId,
                thirdIdentity.OperationId),
            "restoring the exact expected stat revision restores the fixture");

        await executor.DiscardForSessionAsync(
            subject,
            ownership,
            rawCorrelation.ConnectionId);
        var afterDiscard = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            afterDiscard.PendingPreviewOperationId is null &&
            afterDiscard.FeatherStack == 1 &&
            SamePetGrowthValues(afterDiscard, acceptedState),
            "session exit discards preview state without reverting applied stats");

        var fourthIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawCorrelation.ConnectionId);
        var fourth = await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                PetGrowthResetCommandEnvelope.CreateRawLocal(
                    subject,
                    rawCorrelation,
                    DateTimeOffset.UtcNow,
                    new PetGrowthResetCommand(fourthIdentity))));
        Check.True(
            fourth.Receipt?.Status ==
                PetDurableReceiptStatus.PetGrowthPreviewed,
            "expiry fixture creates a final preview");
        await ExpirePetGrowthPreviewAsync(
            dataSource,
            subject.CharacterId,
            fourthIdentity.OperationId);

        var expiredAccept = await executor.ExecuteAsync(
            CreateGrowthAcceptEnvelope(
                subject,
                rawCorrelation,
                fourthIdentity.OperationId));
        var afterExpiry = await ReadPetGrowthResetStateAsync(
            dataSource,
            subject.CharacterId,
            petId);
        Check.True(
            expiredAccept.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            expiredAccept.Receipt?.Status ==
                PetDurableReceiptStatus.PetGrowthPreviewUnavailable &&
            afterExpiry.PendingPreviewOperationId is null &&
            afterExpiry.FeatherStack == 0 &&
            SamePetGrowthValues(afterExpiry, acceptedState),
            "expired OK fails closed and removes only its stale preview");
        return afterExpiry;
    }

    private static async Task ExpirePetGrowthPreviewAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        Guid previewOperationId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pet_growth_previews
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
            "expire exactly one Phoenix Growth preview");
    }

    private static async Task AdvanceGrowthStatRevisionAsync(
        NpgsqlDataSource dataSource,
        long petId,
        short statCode)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pet_stat_values
            SET revision = revision + 1
            WHERE pet_id = @petId
              AND stat_code = @statCode;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("statCode", statCode);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "advance exactly one Growth stat revision");
    }

    private static async Task RestoreGrowthStatRevisionAsync(
        NpgsqlDataSource dataSource,
        long petId,
        short statCode)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_pet_stat_values
            SET revision = revision - 1
            WHERE pet_id = @petId
              AND stat_code = @statCode
              AND revision > 0;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("statCode", statCode);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "restore exactly one Growth stat revision");
    }
}
