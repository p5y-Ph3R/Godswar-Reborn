using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

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

    private static async Task RepairMutableChampionTalentAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predecessorRevision,
        CancellationToken cancellationToken)
    {
        var state = await ReadMutableChampionTalentAuthorityStateAsync(
            connection,
            transaction,
            predecessorRevision,
            cancellationToken);
        if (state == ChampionTalentAuthorityState.Corrected)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            WITH correction(
                id, effect_value, effect_text,
                inflated_value, inflated_text
            ) AS (
                SELECT *
                FROM unnest(
                    @talent_ids::integer[],
                    @effect_values::numeric[],
                    @effect_texts::text[],
                    @inflated_values::numeric[],
                    @inflated_texts::text[])
            )
            UPDATE talent_templates mutable
            SET effect_value = correction.effect_value,
                stats = jsonb_set(
                    mutable.stats,
                    ARRAY[mutable.effect_type],
                    to_jsonb((mutable.effect_id::text || ',' ||
                        correction.effect_text)::text),
                    false)
            FROM gameplay_talent_definitions predecessor
            JOIN correction ON correction.id = predecessor.id
            WHERE predecessor.revision = @predecessor_revision
              AND predecessor.class_id = 1
              AND mutable.id = predecessor.id
              AND mutable.class_id = predecessor.class_id
              AND mutable.effect_id = predecessor.effect_id
              AND mutable.effect_type = predecessor.effect_type
              AND mutable.effect_value = correction.inflated_value
              AND mutable.stats ->> mutable.effect_type =
                  mutable.effect_id::text || ',' ||
                  correction.inflated_text;
            """,
            connection,
            transaction);
        AddChampionCorrectionParameters(command);
        command.Parameters.AddWithValue(
            "predecessor_revision",
            NpgsqlDbType.Varchar,
            predecessorRevision);
        var repaired = await command.ExecuteNonQueryAsync(cancellationToken);
        if (repaired != ChampionTalentScalars.Length)
        {
            throw ChampionUpgradeUnavailable(
                $"Champion authority repaired {repaired} mutable talents; " +
                $"expected {ChampionTalentScalars.Length}.");
        }
    }

    private static async Task<ChampionTalentAuthorityState>
        ReadMutableChampionTalentAuthorityStateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string predecessorRevision,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH correction(
                id, effect_value, effect_text,
                inflated_value, inflated_text
            ) AS (
                SELECT *
                FROM unnest(
                    @talent_ids::integer[],
                    @effect_values::numeric[],
                    @effect_texts::text[],
                    @inflated_values::numeric[],
                    @inflated_texts::text[])
            )
            SELECT
                COUNT(*) FILTER (
                    WHERE mutable.effect_value = correction.effect_value
                      AND mutable.stats ->> mutable.effect_type =
                          mutable.effect_id::text || ',' ||
                          correction.effect_text)::integer,
                COUNT(*) FILTER (
                    WHERE mutable.effect_value = correction.inflated_value
                      AND mutable.stats ->> mutable.effect_type =
                          mutable.effect_id::text || ',' ||
                          correction.inflated_text)::integer
            FROM correction
            LEFT JOIN gameplay_talent_definitions predecessor
              ON predecessor.revision = @predecessor_revision
             AND predecessor.class_id = 1
             AND predecessor.id = correction.id
            LEFT JOIN talent_templates mutable
              ON mutable.id = predecessor.id
             AND mutable.class_id = predecessor.class_id
             AND mutable.effect_id = predecessor.effect_id
             AND mutable.effect_type = predecessor.effect_type;
            """,
            connection,
            transaction);
        AddChampionCorrectionParameters(command);
        command.Parameters.AddWithValue(
            "predecessor_revision",
            NpgsqlDbType.Varchar,
            predecessorRevision);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw ChampionUpgradeUnavailable(
                "Mutable Champion talent authority returned no audit row.");
        }

        var corrected = reader.GetInt32(0);
        var inflated = reader.GetInt32(1);
        return (corrected, inflated) switch
        {
            (19, 0) => ChampionTalentAuthorityState.Corrected,
            (0, 19) => ChampionTalentAuthorityState.Inflated,
            _ => throw ChampionUpgradeUnavailable(
                "Mutable Champion talents are missing, mixed, or drifted " +
                $"(corrected={corrected}, inflated={inflated}).")
        };
    }

    private static void AddChampionCorrectionParameters(NpgsqlCommand command)
    {
        command.Parameters.AddWithValue(
            "talent_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Integer,
            ChampionTalentScalars.Select(static value => value.Id).ToArray());
        command.Parameters.AddWithValue(
            "effect_values",
            NpgsqlDbType.Array | NpgsqlDbType.Numeric,
            ChampionTalentScalars.Select(static value => value.Value).ToArray());
        command.Parameters.AddWithValue(
            "effect_texts",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            ChampionTalentScalars.Select(static value =>
                FormatScalar(value.Value)).ToArray());
        command.Parameters.AddWithValue(
            "inflated_values",
            NpgsqlDbType.Array | NpgsqlDbType.Numeric,
            ChampionTalentScalars.Select(static value =>
                value.Value * ChampionTooltipScale).ToArray());
        command.Parameters.AddWithValue(
            "inflated_texts",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            ChampionTalentScalars.Select(static value =>
                FormatScalar(value.Value * ChampionTooltipScale)).ToArray());
    }
}
