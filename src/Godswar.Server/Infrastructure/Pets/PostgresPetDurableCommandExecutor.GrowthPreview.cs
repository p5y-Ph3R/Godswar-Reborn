using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private static readonly TimeSpan PetGrowthPreviewLifetime =
        TimeSpan.FromMinutes(2);

    private async Task<PetTransition> ExecutePetGrowthPreviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetGrowthResetCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        var pet = await LockSummonedPetForGrowthResetAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null)
        {
            return new(PetDurableReceiptStatus.PetNotTaken);
        }
        ValidateGrowthResetPet(pet);

        var feather = await LockFirstPhoenixFeatherAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (feather is null)
        {
            return FromGrowthResetPet(
                PetDurableReceiptStatus.PhoenixFeatherNotFound,
                pet);
        }

        var stats = await LockPetGrowthStatsAsync(
            connection,
            transaction,
            pet.PetId,
            pet.Level,
            cancellationToken);
        var random = new Random(
            RandomNumberGenerator.GetInt32(int.MaxValue));
        var roll = _petContent.RollGrowth(pet.Aptitude, random);
        var rebirthModifier = PetPhoenixRebirthModifierPolicy.Roll(
            pet.CompletedRebirths,
            random);
        var rates = ToGrowthArray(roll.Rates);
        var modifiers = ToGrowthArray(rebirthModifier);
        ValidateCountWidenedNatureRoll(pet, rates);
        var expiresAtUtc = await UpsertGrowthPreviewAsync(
            connection,
            transaction,
            envelope,
            pet,
            stats,
            rates,
            modifiers,
            cancellationToken);

        var consumed = await ConsumeOneStackItemAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            feather.BagSlot,
            feather.Item,
            cancellationToken);
        var inventoryRevision = await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            character.InventoryRevision,
            cancellationToken);
        await WritePetGrowthPreviewAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            feather,
            stats,
            rates,
            modifiers,
            roll.TotalGrowth,
            cancellationToken);

        return new PetTransition(
            PetDurableReceiptStatus.PetGrowthPreviewed,
            KitBagSlot: feather.BagSlot,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            IsCarried: true,
            IsSummoned: true,
            InventoryMutations:
            [
                new InventoryMutation(
                    feather.Item.ItemId,
                    consumed.MutationKind,
                    feather.Item.BeforeState,
                    consumed.AfterState,
                    "pet_growth_preview",
                    inventoryRevision)
            ],
            GrowthPreview: new PetGrowthPreviewSnapshot(
                envelope.Command.Identity.OperationId,
                pet.PetId,
                pet.Level,
                pet.Revision,
                roll.Rates,
                expiresAtUtc,
                ToGrowthVector(stats),
                PetGrowthPreviewRateSemantics
                    .NatureBaseWithRebirthModifier,
                pet.CompletedRebirths,
                ToGrowthVector(rebirthModifier)));
    }

    private async Task<PetTransition> ExecutePetGrowthAcceptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetGrowthResetCommand> envelope,
        CancellationToken cancellationToken)
    {
        var preview = await LockGrowthPreviewAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (preview is null ||
            preview.PreviewOperationId !=
                envelope.Command.PreviewOperationId ||
            preview.ConnectionId != envelope.Connection.ConnectionId ||
            preview.OwnerId != envelope.Ownership.OwnerId ||
            preview.OwnerGeneration != envelope.Ownership.Generation)
        {
            return new(PetDurableReceiptStatus.PetGrowthPreviewUnavailable);
        }
        ValidateLockedGrowthPreview(preview);
        if (preview.Expired)
        {
            await DeleteGrowthPreviewAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                preview.PreviewOperationId,
                cancellationToken);
            return new(PetDurableReceiptStatus.PetGrowthPreviewUnavailable);
        }

        var pet = await LockSummonedPetForGrowthResetAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (pet is null ||
            pet.PetId != preview.PetId ||
            pet.Level != preview.ExpectedPetLevel ||
            pet.Revision != preview.ExpectedPetRevision ||
            preview.UsesRebirthCountWidenedRates &&
                pet.CompletedRebirths != preview.CompletedRebirths)
        {
            await DeleteGrowthPreviewAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                preview.PreviewOperationId,
                cancellationToken);
            return new(PetDurableReceiptStatus.PetGrowthPreviewUnavailable);
        }
        ValidateGrowthResetPet(pet);
        if (preview.UsesRebirthCountWidenedRates)
        {
            ValidateCountWidenedNatureRoll(pet, preview.GrowthRates);
        }

        var stats = await LockPetGrowthStatsAsync(
            connection,
            transaction,
            pet.PetId,
            pet.Level,
            cancellationToken);
        if (!stats.Select(static value => value.Revision)
                .SequenceEqual(preview.ExpectedStatRevisions))
        {
            await DeleteGrowthPreviewAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                preview.PreviewOperationId,
                cancellationToken);
            return new(PetDurableReceiptStatus.PetGrowthPreviewUnavailable);
        }

        var nextStats = new GrowthResetStat[stats.Count];
        for (var index = 0; index < stats.Count; index++)
        {
            var current = stats[index];
            var nextAcceleration = preview.UsesRebirthCountWidenedRates
                ? preview.RebirthModifiers![index]
                : current.GrowthAcceleration;
            nextStats[index] = current with
            {
                AddedSavvy =
                    PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                        pet.Level,
                        preview.GrowthRates[index],
                        nextAcceleration),
                BaseGrowthRate = preview.GrowthRates[index],
                GrowthAcceleration = nextAcceleration,
                Revision = checked(current.Revision + 1)
            };
            await UpdatePetGrowthStatAsync(
                connection,
                transaction,
                pet.PetId,
                current,
                nextStats[index],
                cancellationToken);
        }

        var nextPetRevision = await MarkPetGrowthRevealedAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            pet,
            cancellationToken);
        await DeleteGrowthPreviewAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            preview.PreviewOperationId,
            cancellationToken);
        await WritePetGrowthAcceptAuditAsync(
            connection,
            transaction,
            envelope,
            pet,
            preview,
            stats,
            nextStats,
            cancellationToken);

        return new PetTransition(
            PetDurableReceiptStatus.PetGrowthAccepted,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: nextPetRevision,
            IsCarried: true,
            IsSummoned: true);
    }

    private void ValidateGrowthResetPet(LockedGrowthResetPet pet)
    {
        if (!_petContent.TryGetAptitude(pet.Aptitude, out _))
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has unknown aptitude {pet.Aptitude}.");
        }
        if (!string.Equals(
                pet.InitialSavvySourceVersion,
                PetSavvyRuntimeSemantics.SourceVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has invalid Growth provenance.");
        }
    }

    private async Task<DateTimeOffset> UpsertGrowthPreviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetGrowthResetCommand> envelope,
        LockedGrowthResetPet pet,
        IReadOnlyList<GrowthResetStat> stats,
        decimal[] rates,
        decimal[] rebirthModifiers,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_pet_growth_previews (
                user_id, pet_id, preview_operation_id, connection_id,
                owner_id, owner_generation, expected_pet_level,
                expected_pet_revision, expected_stat_revisions,
                growth_rates, rate_semantics, completed_rebirths,
                rebirth_modifiers, expires_at
            )
            VALUES (
                @characterId, @petId, @previewOperationId, @connectionId,
                @ownerId, @ownerGeneration, @petLevel,
                @petRevision, @statRevisions, @growthRates,
                @rateSemantics, @completedRebirths, @rebirthModifiers,
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
                growth_rates = EXCLUDED.growth_rates,
                rate_semantics = EXCLUDED.rate_semantics,
                completed_rebirths = EXCLUDED.completed_rebirths,
                rebirth_modifiers = EXCLUDED.rebirth_modifiers,
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
        command.Parameters.Add(
            "growthRates",
            NpgsqlDbType.Array | NpgsqlDbType.Numeric).Value = rates;
        command.Parameters.AddWithValue(
            "rateSemantics",
            CountWidenedRateSemantics);
        command.Parameters.AddWithValue(
            "completedRebirths",
            pet.CompletedRebirths);
        command.Parameters.Add(
            "rebirthModifiers",
            NpgsqlDbType.Array | NpgsqlDbType.Numeric).Value =
            rebirthModifiers;
        command.Parameters.AddWithValue("lifetime", PetGrowthPreviewLifetime);
        var expires = await command.ExecuteScalarAsync(cancellationToken);
        return expires is DateTime value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : throw new InvalidDataException(
                "The pet Growth preview expiry was not returned.");
    }

    private async Task<LockedPetGrowthPreview?> LockGrowthPreviewAsync(
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
                   growth_rates, expires_at <= clock_timestamp(),
                   rate_semantics, completed_rebirths,
                   rebirth_modifiers
            FROM public.character_pet_growth_previews
            WHERE user_id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedPetGrowthPreview(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetInt64(4),
                reader.GetInt16(5),
                reader.GetInt64(6),
                reader.GetFieldValue<long[]>(7),
                reader.GetFieldValue<decimal[]>(8),
                reader.GetBoolean(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetInt16(11),
                reader.IsDBNull(12)
                    ? null
                    : reader.GetFieldValue<decimal[]>(12))
            : null;
    }

    private async Task DeleteGrowthPreviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        Guid previewOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.character_pet_growth_previews
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
                "The pet Growth preview was not removed exactly once.");
        }
    }

    private sealed record LockedPetGrowthPreview(
        long PetId,
        Guid PreviewOperationId,
        Guid ConnectionId,
        Guid OwnerId,
        long OwnerGeneration,
        short ExpectedPetLevel,
        long ExpectedPetRevision,
        long[] ExpectedStatRevisions,
        decimal[] GrowthRates,
        bool Expired,
        string RateSemantics,
        short? CompletedRebirths,
        decimal[]? RebirthModifiers)
    {
        public bool UsesRebirthCountWidenedRates =>
            string.Equals(
                RateSemantics,
                CountWidenedRateSemantics,
                StringComparison.Ordinal);
    }
}
