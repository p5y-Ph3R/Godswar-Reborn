using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetGrowthResetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetGrowthResetCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        return envelope.Command.Operation switch
        {
            PetGrowthResetOperation.Preview =>
                await ExecutePetGrowthPreviewAsync(
                    connection,
                    transaction,
                    envelope,
                    character,
                    cancellationToken),
            PetGrowthResetOperation.Accept =>
                await ExecutePetGrowthAcceptAsync(
                    connection,
                    transaction,
                    envelope,
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The pet Growth operation is unsupported.")
        };
    }

    private async Task<LockedGrowthResetPet?>
        LockSummonedPetForGrowthResetAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, aptitude, level, experience, revision,
                   growth_revealed, initial_savvy_source_version,
                   completed_rebirths
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
              AND NOT contributes_to_character
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var pet = new LockedGrowthResetPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt16(7));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one summoned pet is authoritative.");
        }
        return pet;
    }

    private async Task<LockedPhoenixFeather?> LockFirstPhoenixFeatherAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, slot_index, prop_id, item_quality, bound, stack,
                   to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @propId
              AND stack > 0
            ORDER BY slot_index
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "propId",
            checked((int)PetItemCatalog.PhoenixFeather));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedPhoenixFeather(
                reader.GetInt16(1),
                new LockedBagItem(
                    reader.GetInt64(0),
                    reader.GetInt32(2),
                    reader.GetInt16(3),
                    reader.GetInt16(4) != 0,
                    reader.GetInt16(5),
                    reader.GetString(6)))
            : null;
    }

    private async Task<IReadOnlyList<GrowthResetStat>>
        LockPetGrowthStatsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long petId,
            short petLevel,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT stat_code, added_savvy, base_growth_rate,
                   growth_acceleration, revision
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        var rows = new List<GrowthResetStat>(6);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GrowthResetStat(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetInt64(4)));
        }
        if (rows.Count != 6 ||
            rows.Where((row, index) => row.StatCode != index + 1).Any() ||
            rows.Any(row =>
                row.BaseGrowthRate <= 0 ||
                row.GrowthAcceleration < 0 ||
                row.AddedSavvy !=
                    PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                        petLevel,
                        row.BaseGrowthRate,
                        row.GrowthAcceleration) ||
                row.Revision < 0))
        {
            throw new InvalidDataException(
                $"Pet {petId} does not have one complete Growth vector.");
        }
        return rows;
    }

    private async Task UpdatePetGrowthStatAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        GrowthResetStat before,
        GrowthResetStat after,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pet_stat_values
            SET added_savvy = @addedSavvy,
                base_growth_rate = @baseGrowthRate,
                growth_acceleration = @newGrowthAcceleration,
                revision = revision + 1
            WHERE pet_id = @petId
              AND stat_code = @statCode
              AND added_savvy = @oldAddedSavvy
              AND base_growth_rate = @oldBaseGrowthRate
              AND growth_acceleration = @oldGrowthAcceleration
              AND revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("addedSavvy", after.AddedSavvy);
        command.Parameters.AddWithValue(
            "baseGrowthRate",
            after.BaseGrowthRate);
        command.Parameters.AddWithValue(
            "newGrowthAcceleration",
            after.GrowthAcceleration);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("statCode", before.StatCode);
        command.Parameters.AddWithValue("oldAddedSavvy", before.AddedSavvy);
        command.Parameters.AddWithValue(
            "oldBaseGrowthRate",
            before.BaseGrowthRate);
        command.Parameters.AddWithValue(
            "oldGrowthAcceleration",
            before.GrowthAcceleration);
        command.Parameters.AddWithValue("revision", before.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                $"Pet {petId} Growth stat {before.StatCode} was not reset exactly once.");
        }
    }

    private async Task<long> MarkPetGrowthRevealedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedGrowthResetPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET growth_revealed = true,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND growth_revealed = @growthRevealed
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
            RETURNING revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("revision", pet.Revision);
        command.Parameters.AddWithValue(
            "growthRevealed",
            pet.GrowthRevealed);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(pet.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "The pet Growth revision was not advanced exactly once.");
    }

    private async Task WritePetGrowthResetAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetGrowthResetCommand> envelope,
        LockedGrowthResetPet pet,
        LockedPhoenixFeather feather,
        IReadOnlyList<GrowthResetStat> before,
        IReadOnlyList<GrowthResetStat> after,
        decimal totalGrowth,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.pet_operation_audit (
                request_id, user_id, user_id_snapshot,
                pet_id, pet_id_snapshot, operation, outcome,
                before_state, after_state, consumed_items
            )
            VALUES (
                @requestId, @characterId, @characterId,
                @petId, @petId, 'reveal_growth', 'committed',
                jsonb_build_object(
                    'growth_revealed', @oldGrowthRevealed,
                    'growth', @beforeGrowth::jsonb),
                jsonb_build_object(
                    'growth_revealed', true,
                    'aptitude', @aptitude,
                    'total_growth', @totalGrowth,
                    'growth', @afterGrowth::jsonb),
                jsonb_build_array(jsonb_build_object(
                    'item_id', @itemId,
                    'item_instance_id', @itemInstanceId,
                    'quantity', 1,
                    'kit_bag_slot', @kitBagSlot))
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "requestId",
            envelope.Command.Identity.OperationId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue(
            "oldGrowthRevealed",
            pet.GrowthRevealed);
        command.Parameters.AddWithValue(
            "beforeGrowth",
            SerializeGrowth(before));
        command.Parameters.AddWithValue("aptitude", pet.Aptitude);
        command.Parameters.AddWithValue("totalGrowth", totalGrowth);
        command.Parameters.AddWithValue(
            "afterGrowth",
            SerializeGrowth(after));
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)PetItemCatalog.PhoenixFeather));
        command.Parameters.AddWithValue(
            "itemInstanceId",
            feather.Item.ItemId);
        command.Parameters.AddWithValue("kitBagSlot", feather.BagSlot);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The committed pet Growth reset was not audited exactly once.");
        }
    }

    private static string SerializeGrowth(
        IReadOnlyList<GrowthResetStat> values) =>
        System.Text.Json.JsonSerializer.Serialize(
            values.Select(static value => new
            {
                stat_code = value.StatCode,
                added_value = value.AddedSavvy,
                base_growth_rate = value.BaseGrowthRate,
                growth_acceleration = value.GrowthAcceleration,
                revision = value.Revision
            }));

    private static decimal[] ToGrowthArray(PetContentStatVector value) =>
    [
        value.Agility,
        value.Strength,
        value.Accuracy,
        value.Technique,
        value.Wisdom,
        value.Luck
    ];

    private static PetContentStatVector ToGrowthVector(
        IReadOnlyList<GrowthResetStat> values) =>
        new(
            values[0].BaseGrowthRate,
            values[1].BaseGrowthRate,
            values[2].BaseGrowthRate,
            values[3].BaseGrowthRate,
            values[4].BaseGrowthRate,
            values[5].BaseGrowthRate);

    private static PetTransition FromGrowthResetPet(
        PetDurableReceiptStatus status,
        LockedGrowthResetPet pet) =>
        new(
            status,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            IsCarried: true,
            IsSummoned: true);

    private sealed record LockedGrowthResetPet(
        long PetId,
        short Aptitude,
        short Level,
        long Experience,
        long Revision,
        bool GrowthRevealed,
        string? InitialSavvySourceVersion,
        short CompletedRebirths);

    private sealed record LockedPhoenixFeather(
        int BagSlot,
        LockedBagItem Item);

    private sealed record GrowthResetStat(
        short StatCode,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal GrowthAcceleration,
        long Revision);
}
