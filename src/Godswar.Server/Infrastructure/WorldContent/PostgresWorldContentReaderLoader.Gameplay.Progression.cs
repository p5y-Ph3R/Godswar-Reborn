using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    private static async Task<GameplayClassDefinition[]>
        ReadGameplayClassesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayClassDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT id, name, display_name, source
            FROM gameplay_class_definitions
            WHERE revision = @revision
            ORDER BY id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayClassDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayTalentEffectDefinition[]>
        ReadGameplayTalentEffectsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayTalentEffectDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT id, key, display_name, percent
            FROM gameplay_talent_effect_definitions
            WHERE revision = @revision
            ORDER BY id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayTalentEffectDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayTalentDefinition[]>
        ReadGameplayTalentsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayTalentDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT id, class_id, tree_order, name, prefix_id,
                   required_prefix_rank, required_total_rank, equip_request,
                   effect_type, effect_id, effect_value, is_percent,
                   icon_x, icon_y, icon_width, icon_height, stats::text
            FROM gameplay_talent_definitions
            WHERE revision = @revision
            ORDER BY id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayTalentDefinition(
                reader.GetInt32(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetInt16(9),
                reader.GetDecimal(10),
                reader.GetBoolean(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetInt32(15),
                reader.GetString(16)));
        }

        return values.ToArray();
    }

    private static async Task<GameplaySkillBookDefinition[]>
        ReadGameplaySkillBooksAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplaySkillBookDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT item_id, name_key, display_name, skill_id, base_name,
                   skill_level, class_ids, min_level, max_level,
                   previous_skill_id, stats::text
            FROM gameplay_skill_book_definitions
            WHERE revision = @revision
            ORDER BY item_id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplaySkillBookDefinition(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt16(5),
                reader.GetFieldValue<short[]>(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.GetString(10)));
        }

        return values.ToArray();
    }
}
