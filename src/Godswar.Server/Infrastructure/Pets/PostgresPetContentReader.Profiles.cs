using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentReader
{
    private static async Task<IReadOnlyList<PetNativeProfileContentDefinition>>
        ReadProfilesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetNativeProfileContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT species_id, aptitude,
                   starting_agility, starting_strength, starting_accuracy,
                   starting_technique, starting_wisdom, starting_luck,
                   genius_agility, genius_strength, genius_accuracy,
                   genius_technique, genius_wisdom, genius_luck,
                   native_quality, native_samsara, native_genius,
                   starter_skill_id, native_skill_count, native_procreate,
                   lifetime
            FROM pet_content_native_profiles
            WHERE revision = @revision
            ORDER BY species_id, aptitude;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetNativeProfileContentDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                new PetContentStatVector(
                    reader.GetDecimal(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6),
                    reader.GetDecimal(7)),
                new PetContentStatVector(
                    reader.GetDecimal(8),
                    reader.GetDecimal(9),
                    reader.GetDecimal(10),
                    reader.GetDecimal(11),
                    reader.GetDecimal(12),
                    reader.GetDecimal(13)),
                reader.GetInt32(14),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                reader.GetInt32(18),
                reader.GetInt32(19),
                reader.GetInt32(20)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<PetExperienceStepContentDefinition>>
        ReadExperienceAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetExperienceStepContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT current_level, required_experience
            FROM pet_content_experience_steps
            WHERE revision = @revision
            ORDER BY current_level;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetExperienceStepContentDefinition(
                reader.GetInt16(0),
                reader.GetInt32(1)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<PetRebirthStepContentDefinition>>
        ReadRebirthAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetRebirthStepContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT rebirth_number, required_pet_level,
                   chance_item_id, chance_item_name,
                   minimum_increase_per_stat, maximum_increase_per_stat
            FROM pet_content_rebirth_steps
            WHERE revision = @revision
            ORDER BY rebirth_number;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetRebirthStepContentDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                checked((uint)reader.GetInt32(2)),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5)));
        }
        return values;
    }
}
