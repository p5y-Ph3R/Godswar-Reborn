using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresGameplayContentPublisher
{
    private static async Task CopyProgressionDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        GameplayContentCatalog content,
        CancellationToken cancellationToken)
    {
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_class_definitions (
                revision, id, name, display_name, source
            )
            SELECT @revision, id, name, display_name, source
            FROM class_templates
            ORDER BY id;
            """,
            content.Classes.Count,
            "classes",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_talent_effect_definitions (
                revision, id, key, display_name, percent
            )
            SELECT @revision, id, key, display_name, percent
            FROM talent_effect_templates
            ORDER BY id;
            """,
            content.TalentEffects.Count,
            "talent effects",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_talent_definitions (
                revision, id, class_id, tree_order, name, prefix_id,
                required_prefix_rank, required_total_rank, equip_request,
                effect_type, effect_id, effect_value, is_percent,
                icon_x, icon_y, icon_width, icon_height, stats
            )
            SELECT @revision, id, class_id, tree_order, name, prefix_id,
                   required_prefix_rank, required_total_rank, equip_request,
                   effect_type, effect_id, effect_value, is_percent,
                   icon_x, icon_y, icon_width, icon_height, stats
            FROM talent_templates
            ORDER BY id;
            """,
            content.Talents.Count,
            "talents",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_skill_book_definitions (
                revision, item_id, name_key, display_name, skill_id,
                base_name, skill_level, class_ids, min_level, max_level,
                previous_skill_id, stats
            )
            SELECT @revision, item_id, name_key, display_name, skill_id,
                   base_name, skill_level, class_ids, min_level, max_level,
                   previous_skill_id, stats
            FROM skill_book_templates
            ORDER BY item_id;
            """,
            content.SkillBooks.Count,
            "skill books",
            cancellationToken);
    }
}
