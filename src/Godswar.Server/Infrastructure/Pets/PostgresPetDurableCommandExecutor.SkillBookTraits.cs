using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetSavvy> LockSkillBookPetTraitsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockedSkillBookPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT stat_code, initial_savvy, added_savvy,
                   base_growth_rate, growth_acceleration,
                   birth_initial_savvy, rarity_added_savvy
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        var rows = new List<LockedSkillBookTrait>(6);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LockedSkillBookTrait(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6)));
        }

        return ResolveSkillBookTraits(pet, rows);
    }

    private static PetSavvy ResolveSkillBookTraits(
        LockedSkillBookPet pet,
        IReadOnlyList<LockedSkillBookTrait> rows)
    {
        PetSavvyRuntimeSemantics.ValidateProjectionSourceVersion(
            pet.InitialSavvySourceVersion);
        if (rows.Count != 6 ||
            rows.Where((row, index) => row.StatCode != index + 1).Any())
        {
            throw new InvalidDataException(
                "The carried pet has no complete Savvy projection.");
        }

        var initial = ToSavvy(rows.Select(static row => row.Initial));
        var added = ToSavvy(rows.Select(static row => row.Added));
        var growth = ToSavvy(rows.Select(static row => row.Growth));
        var acceleration = ToSavvy(
            rows.Select(static row => row.Acceleration));
        var hasCurrentProvenance =
            pet.InitialSavvySourceVersion is not null;
        if (rows.Any(row => hasCurrentProvenance
                ? row.Birth is null || row.Rarity is null ||
                  row.Birth <= 0m || row.Birth != row.Rarity
                : row.Birth is not null || row.Rarity is not null))
        {
            throw new InvalidDataException(
                "The carried pet has partial Savvy provenance.");
        }

        PetSavvy? rarity = hasCurrentProvenance
            ? ToSavvy(rows.Select(static row => row.Rarity!.Value))
            : null;
        var rawTotal = PetSavvyRuntimeSemantics.ResolvePlayerVisibleTotal(
            pet.Level,
            initial,
            added,
            growth,
            acceleration,
            rarity);
        return PetSoulContractPolicy.ResolveDisplayedTotal(
            rawTotal,
            pet.SoulContractStage);
    }

    private static PetSavvy ToSavvy(IEnumerable<decimal> values)
    {
        var ordered = values.ToArray();
        return ordered.Length == 6
            ? new PetSavvy(
                ordered[0], ordered[1], ordered[2],
                ordered[3], ordered[4], ordered[5])
            : throw new InvalidDataException(
                "A pet Savvy vector is incomplete.");
    }
}
