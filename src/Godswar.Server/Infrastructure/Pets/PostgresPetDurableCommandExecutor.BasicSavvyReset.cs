using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> ExecutePetBasicSavvyResetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetBasicSavvyResetCommand> envelope,
        LockedCharacter character,
        CancellationToken cancellationToken) =>
        envelope.Command.Operation switch
        {
            PetBasicSavvyResetOperation.Preview =>
                await ExecutePetBasicSavvyCommitAsync(
                    connection,
                    transaction,
                    envelope,
                    character,
                    cancellationToken),
            PetBasicSavvyResetOperation.Accept =>
                new PetTransition(
                    PetDurableReceiptStatus
                        .PetBasicSavvyPreviewUnavailable),
            _ => throw new InvalidDataException(
                "The pet Basic-Savvy operation is unsupported.")
        };

    private async Task<LockedBasicSavvyResetPet?>
        LockSummonedPetForBasicSavvyResetAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, level, experience, revision,
                   initial_savvy_baseline_total,
                   completed_pet_merges,
                   initial_savvy_source_version
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

        var pet = new LockedBasicSavvyResetPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one summoned pet is authoritative.");
        }
        ValidateBasicSavvyResetPet(pet);
        return pet;
    }

    private async Task<LockedFairyFeather?> LockFirstFairyFeatherAsync(
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
            ORDER BY slot_index, id
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "propId",
            checked((int)PetItemCatalog.FairyFeather));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedFairyFeather(
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

    private async Task<IReadOnlyList<BasicSavvyResetStat>>
        LockPetBasicSavvyStatsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            LockedBasicSavvyResetPet pet,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT stat_code, initial_savvy,
                   birth_initial_savvy, rarity_added_savvy, revision
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        var rows = new List<BasicSavvyResetStat>(6);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BasicSavvyResetStat(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.GetInt64(4)));
        }
        ValidateBasicSavvyStats(pet, rows);
        return rows;
    }

    private static void ValidateBasicSavvyResetPet(
        LockedBasicSavvyResetPet pet)
    {
        if (pet.PetId <= 0 ||
            pet.Level is < 1 or > 120 ||
            pet.Experience < 0 ||
            pet.Revision < 0 ||
            pet.HatchBaselineTotal is null or <= 0 ||
            pet.CompletedPetMerges < 0 ||
            !string.Equals(
                pet.InitialSavvySourceVersion,
                PetSavvyPersistenceContract.SourceVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has invalid Basic-Savvy provenance.");
        }
    }

    private static void ValidateBasicSavvyStats(
        LockedBasicSavvyResetPet pet,
        IReadOnlyList<BasicSavvyResetStat> rows)
    {
        if (rows.Count != 6 ||
            rows.Where((row, index) => row.StatCode != index + 1).Any() ||
            rows.Any(static row =>
                row.InitialSavvy <= 0m ||
                !IsExactHundredth(row.InitialSavvy) ||
                row.BirthInitialSavvy is null or <= 0m ||
                row.RarityAddedSavvy is null or <= 0m ||
                row.BirthInitialSavvy != row.RarityAddedSavvy ||
                row.Revision < 0))
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} does not have one complete Basic-Savvy vector.");
        }

        var birthTotal = rows.Sum(static row =>
            row.BirthInitialSavvy!.Value);
        var currentTotal = rows.Sum(static row => row.InitialSavvy);
        if (birthTotal != pet.HatchBaselineTotal ||
            currentTotal < birthTotal ||
            !IsExactHundredth(currentTotal))
        {
            throw new InvalidDataException(
                $"Pet {pet.PetId} has inconsistent aggregate Basic-Savvy provenance.");
        }
    }

    private static PetContentStatVector ToBasicSavvyVector(
        IReadOnlyList<BasicSavvyResetStat> stats) =>
        new(
            stats[0].InitialSavvy,
            stats[1].InitialSavvy,
            stats[2].InitialSavvy,
            stats[3].InitialSavvy,
            stats[4].InitialSavvy,
            stats[5].InitialSavvy);

    private static PetContentStatVector ToBasicSavvyVector(
        IReadOnlyList<decimal> values) =>
        values.Count == 6
            ? new(
                values[0], values[1], values[2],
                values[3], values[4], values[5])
            : throw new InvalidDataException(
                "A Basic-Savvy vector must contain exactly six values.");

    private static decimal[] ToBasicSavvyArray(
        PetContentStatVector value) =>
    [
        value.Agility,
        value.Strength,
        value.Accuracy,
        value.Technique,
        value.Wisdom,
        value.Luck
    ];

    private static bool IsExactHundredth(decimal value) =>
        value * 100m == decimal.Truncate(value * 100m);

    private static PetTransition FromBasicSavvyResetPet(
        PetDurableReceiptStatus status,
        LockedBasicSavvyResetPet pet) =>
        new(
            status,
            PetId: pet.PetId,
            PetLevel: pet.Level,
            PetExperience: pet.Experience,
            PetRevision: pet.Revision,
            IsCarried: true,
            IsSummoned: true);

    private sealed record LockedBasicSavvyResetPet(
        long PetId,
        short Level,
        long Experience,
        long Revision,
        int? HatchBaselineTotal,
        int CompletedPetMerges,
        string? InitialSavvySourceVersion);

    private sealed record LockedFairyFeather(
        int BagSlot,
        LockedBagItem Item);

    private sealed record BasicSavvyResetStat(
        short StatCode,
        decimal InitialSavvy,
        decimal? BirthInitialSavvy,
        decimal? RarityAddedSavvy,
        long Revision);
}
