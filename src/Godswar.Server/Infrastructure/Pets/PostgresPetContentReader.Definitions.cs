using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentReader
{
    private static async Task<PetContentSettings> ReadSettingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateRevisionCommand(
            """
            SELECT minimum_level, maximum_level,
                   maximum_owned_pet_count, maximum_skill_count,
                   minimum_merge_level, minimum_owner_merge_amity,
                   maximum_spirit_items, maximum_rebirth_count,
                   required_rebirth_spirit_count,
                   egg_hatch_runtime_skill_id,
                   merge_spirit_item_id, restricted_merge_spirit_item_id,
                   rebirth_spirit_item_id,
                   restricted_rebirth_spirit_item_id,
                   growth_policy_version,
                   initial_savvy_policy_version,
                   added_savvy_policy_version,
                   added_savvy_weights
            FROM pet_content_settings
            WHERE revision = @revision;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Pet-content revision {revision} has no settings.");
        }

        var settings = new PetContentSettings(
            reader.GetInt16(0),
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt16(3),
            reader.GetInt16(4),
            reader.GetInt16(5),
            reader.GetInt16(6),
            reader.GetInt16(7),
            reader.GetInt16(8),
            reader.GetInt32(9),
            checked((uint)reader.GetInt32(10)),
            checked((uint)reader.GetInt32(11)),
            checked((uint)reader.GetInt32(12)),
            checked((uint)reader.GetInt32(13)),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            Array.AsReadOnly(reader.GetFieldValue<short[]>(17)));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Pet-content revision {revision} has duplicate settings.");
        }
        return settings;
    }

    private static async Task<IReadOnlyList<PetSpeciesContentDefinition>>
        ReadSpeciesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetSpeciesContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT species_id, display_name, food_kind,
                   starter_skill_id, starter_skill_name, lifetime_values,
                   egg_item_id, egg_declared_species_id, magic_jade_item_id
            FROM pet_content_species_definitions
            WHERE revision = @revision
            ORDER BY species_id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetSpeciesContentDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetInt16(2),
                reader.GetInt32(3),
                reader.GetString(4),
                Array.AsReadOnly(reader.GetFieldValue<int[]>(5)),
                reader.IsDBNull(6)
                    ? null
                    : checked((uint)reader.GetInt32(6)),
                reader.IsDBNull(7) ? null : reader.GetInt16(7),
                checked((uint)reader.GetInt32(8))));
        }
        return values;
    }

    private static async Task<IReadOnlyList<PetAptitudeContentDefinition>>
        ReadAptitudesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<PetAptitudeContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT aptitude, name_key, display_name, is_server_extension,
                   minimum_total_growth, maximum_total_growth,
                   maximum_growth_stat_deviation,
                   minimum_initial_savvy, maximum_initial_savvy,
                   maximum_initial_savvy_stat_deviation,
                   minimum_added_savvy, maximum_added_savvy
            FROM pet_content_aptitude_definitions
            WHERE revision = @revision
            ORDER BY aptitude;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new PetAptitudeContentDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetDecimal(9),
                reader.GetInt32(10),
                reader.GetInt32(11)));
        }
        return values;
    }

    private static NpgsqlCommand CreateRevisionCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        return command;
    }
}
