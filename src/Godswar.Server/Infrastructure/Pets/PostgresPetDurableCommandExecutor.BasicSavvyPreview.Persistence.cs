using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<DateTimeOffset> UpsertBasicSavvyPreviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        LockedBasicSavvyResetPet pet,
        IReadOnlyList<BasicSavvyResetStat> stats,
        decimal[] values,
        PetBasicSavvyRedistributionRoll roll,
        CancellationToken cancellationToken)
    {
        var expectedTotal = stats.Sum(static value => value.InitialSavvy);
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_pet_basic_savvy_previews (
                user_id, pet_id, preview_operation_id, connection_id,
                owner_id, owner_generation, expected_pet_level,
                expected_pet_revision, expected_stat_revisions,
                expected_basic_total, basic_savvy_values,
                policy_version, roll_tier, primary_focus,
                secondary_focus, expires_at
            )
            VALUES (
                @characterId, @petId, @previewOperationId, @connectionId,
                @ownerId, @ownerGeneration, @petLevel,
                @petRevision, @statRevisions,
                @expectedBasicTotal, @basicSavvyValues,
                @policyVersion, @rollTier, @primaryFocus,
                @secondaryFocus,
                transaction_timestamp() + @lifetime
            )
            ON CONFLICT (user_id) DO UPDATE SET
                pet_id = EXCLUDED.pet_id,
                preview_operation_id = EXCLUDED.preview_operation_id,
                connection_id = EXCLUDED.connection_id,
                owner_id = EXCLUDED.owner_id,
                owner_generation = EXCLUDED.owner_generation,
                expected_pet_level = EXCLUDED.expected_pet_level,
                expected_pet_revision = EXCLUDED.expected_pet_revision,
                expected_stat_revisions = EXCLUDED.expected_stat_revisions,
                expected_basic_total = EXCLUDED.expected_basic_total,
                basic_savvy_values = EXCLUDED.basic_savvy_values,
                policy_version = EXCLUDED.policy_version,
                roll_tier = EXCLUDED.roll_tier,
                primary_focus = EXCLUDED.primary_focus,
                secondary_focus = EXCLUDED.secondary_focus,
                created_at = transaction_timestamp(),
                expires_at = EXCLUDED.expires_at
            RETURNING expires_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue(
            "previewOperationId",
            envelope.Command.Identity.OperationId);
        command.Parameters.AddWithValue(
            "connectionId",
            envelope.Connection.ConnectionId);
        command.Parameters.AddWithValue("ownerId", envelope.Ownership.OwnerId);
        command.Parameters.AddWithValue(
            "ownerGeneration",
            envelope.Ownership.Generation);
        command.Parameters.AddWithValue("petLevel", pet.Level);
        command.Parameters.AddWithValue("petRevision", pet.Revision);
        command.Parameters.Add(
            "statRevisions",
            NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
            stats.Select(static value => value.Revision).ToArray();
        command.Parameters.AddWithValue(
            "expectedBasicTotal",
            expectedTotal);
        command.Parameters.Add(
            "basicSavvyValues",
            NpgsqlDbType.Array | NpgsqlDbType.Numeric).Value = values;
        command.Parameters.AddWithValue(
            "policyVersion",
            PetBasicSavvyRedistributionPolicy.Version);
        command.Parameters.AddWithValue("rollTier", (short)roll.Tier);
        command.Parameters.AddWithValue(
            "primaryFocus",
            (short)roll.PrimaryFocus);
        command.Parameters.AddWithValue(
            "secondaryFocus",
            (short)roll.SecondaryFocus);
        command.Parameters.AddWithValue(
            "lifetime",
            PetBasicSavvyPreviewLifetime);
        var expires = await command.ExecuteScalarAsync(cancellationToken);
        return expires is DateTime value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : throw new InvalidDataException(
                "The pet Basic-Savvy preview expiry was not returned.");
    }

    private async Task<LockedBasicSavvyPreview?>
        LockBasicSavvyPreviewAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT pet_id, preview_operation_id, connection_id,
                   owner_id, owner_generation, expected_pet_level,
                   expected_pet_revision, expected_stat_revisions,
                   expected_basic_total, basic_savvy_values,
                   policy_version, roll_tier, primary_focus,
                   secondary_focus, expires_at <= clock_timestamp()
            FROM public.character_pet_basic_savvy_previews
            WHERE user_id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedBasicSavvyPreview(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetInt64(4),
                reader.GetInt16(5),
                reader.GetInt64(6),
                reader.GetFieldValue<long[]>(7),
                reader.GetDecimal(8),
                reader.GetFieldValue<decimal[]>(9),
                reader.GetString(10),
                reader.GetInt16(11),
                reader.GetInt16(12),
                reader.GetInt16(13),
                reader.GetBoolean(14))
            : null;
    }

    private static void ValidateStoredBasicSavvyPreview(
        LockedBasicSavvyPreview preview)
    {
        var tier = (PetBasicSavvyRedistributionTier)preview.RollTier;
        var focusIsValid = tier switch
        {
            PetBasicSavvyRedistributionTier.ExtremeSingleFocus or
            PetBasicSavvyRedistributionTier.StrongSingleFocus =>
                preview.PrimaryFocus is >= 1 and <= 6 &&
                preview.SecondaryFocus == 0,
            PetBasicSavvyRedistributionTier.DualFocus =>
                preview.PrimaryFocus is >= 1 and <= 6 &&
                preview.SecondaryFocus is >= 1 and <= 6 &&
                preview.PrimaryFocus != preview.SecondaryFocus,
            PetBasicSavvyRedistributionTier.Balanced =>
                preview.PrimaryFocus == 0 && preview.SecondaryFocus == 0,
            _ => false
        };
        if (preview.PetId <= 0 ||
            preview.PreviewOperationId == Guid.Empty ||
            preview.ConnectionId == Guid.Empty ||
            preview.OwnerId == Guid.Empty ||
            preview.OwnerGeneration <= 0 ||
            preview.ExpectedPetLevel is < 1 or > 120 ||
            preview.ExpectedPetRevision < 0 ||
            preview.ExpectedStatRevisions.Length != 6 ||
            preview.ExpectedStatRevisions.Any(static value => value < 0) ||
            preview.ExpectedBasicTotal <= 0m ||
            !IsExactHundredth(preview.ExpectedBasicTotal) ||
            preview.BasicSavvyValues.Length != 6 ||
            preview.BasicSavvyValues.Any(static value =>
                value <= 0m || !IsExactHundredth(value)) ||
            preview.BasicSavvyValues.Sum() != preview.ExpectedBasicTotal ||
            !string.Equals(
                preview.PolicyVersion,
                PetBasicSavvyRedistributionPolicy.Version,
                StringComparison.Ordinal) ||
            !focusIsValid)
        {
            throw new InvalidDataException(
                "The stored pet Basic-Savvy preview is invalid.");
        }
    }

    private async Task DeleteBasicSavvyPreviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        Guid previewOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.character_pet_basic_savvy_previews
            WHERE user_id = @characterId
              AND preview_operation_id = @previewOperationId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "previewOperationId",
            previewOperationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet Basic-Savvy preview was not removed exactly once.");
        }
    }

    private async Task DeleteAnyBasicSavvyPreviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.character_pet_basic_savvy_previews
            WHERE user_id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdatePetBasicSavvyStatAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        BasicSavvyResetStat before,
        BasicSavvyResetStat after,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pet_stat_values
            SET initial_savvy = @after,
                revision = revision + 1
            WHERE pet_id = @petId
              AND stat_code = @statCode
              AND initial_savvy = @before
              AND birth_initial_savvy = @birthInitialSavvy
              AND rarity_added_savvy = @rarityAddedSavvy
              AND revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("after", after.InitialSavvy);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("statCode", before.StatCode);
        command.Parameters.AddWithValue("before", before.InitialSavvy);
        command.Parameters.AddWithValue(
            "birthInitialSavvy",
            before.BirthInitialSavvy!.Value);
        command.Parameters.AddWithValue(
            "rarityAddedSavvy",
            before.RarityAddedSavvy!.Value);
        command.Parameters.AddWithValue("revision", before.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                $"Pet {petId} Basic-Savvy stat {before.StatCode} was not updated exactly once.");
        }
    }

    private async Task<long> AdvanceBasicSavvyPetRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedBasicSavvyResetPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND level = @petLevel
              AND experience = @petExperience
              AND revision = @petRevision
              AND initial_savvy_baseline_total = @hatchBaselineTotal
              AND completed_pet_merges = @completedPetMerges
              AND initial_savvy_source_version = @sourceVersion
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
              AND NOT contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petLevel", pet.Level);
        command.Parameters.AddWithValue("petExperience", pet.Experience);
        command.Parameters.AddWithValue("petRevision", pet.Revision);
        command.Parameters.AddWithValue(
            "hatchBaselineTotal",
            pet.HatchBaselineTotal!.Value);
        command.Parameters.AddWithValue(
            "completedPetMerges",
            pet.CompletedPetMerges);
        command.Parameters.AddWithValue(
            "sourceVersion",
            pet.InitialSavvySourceVersion!);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The pet Basic-Savvy revision was not advanced exactly once.");
    }

    private sealed record LockedBasicSavvyPreview(
        long PetId,
        Guid PreviewOperationId,
        Guid ConnectionId,
        Guid OwnerId,
        long OwnerGeneration,
        short ExpectedPetLevel,
        long ExpectedPetRevision,
        long[] ExpectedStatRevisions,
        decimal ExpectedBasicTotal,
        decimal[] BasicSavvyValues,
        string PolicyVersion,
        short RollTier,
        short PrimaryFocus,
        short SecondaryFocus,
        bool Expired);
}
