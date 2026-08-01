using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    private static async Task<GameplayMonsterTemplateDefinition[]>
        ReadGameplayMonsterTemplatesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayMonsterTemplateDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT source_key, source_kind, source_map_id, scene_key,
                   template_key, display_name, rank, is_boss, is_elite,
                   is_pet, collision_range
            FROM gameplay_monster_templates
            WHERE revision = @revision
            ORDER BY source_key, template_key;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayMonsterTemplateDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt16(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetFloat(10)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayWorldBossDefinition[]>
        ReadGameplayWorldBossesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayWorldBossDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT map_id, scene_key, template_key, display_name,
                   bonus_basis_points, respawn_interval_seconds
            FROM gameplay_world_boss_definitions
            WHERE revision = @revision
            ORDER BY map_id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayWorldBossDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                TimeSpan.FromSeconds(reader.GetInt32(5))));
        }

        return values.ToArray();
    }

    private static async Task<GameplayPendingWorldBossArea[]>
        ReadGameplayPendingWorldBossesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayPendingWorldBossArea>();
        await using var command = RevisionCommand(
            """
            SELECT map_id, scene_key, reason
            FROM gameplay_pending_world_boss_areas
            WHERE revision = @revision
            ORDER BY map_id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayPendingWorldBossArea(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return values.ToArray();
    }

    private static async Task<GameplaySkillCombatDefinition[]>
        ReadGameplaySkillsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplaySkillCombatDefinition>();
        await using var command = RevisionCommand(
            """
            SELECT skill_id, target, affect_obj, distance, effect_range,
                   property, mp, power1, power2, cast_time_seconds,
                   cooldown_seconds, display_name, base_name, skill_level,
                   class_ids, previous_skill_id, min_level, max_level,
                   description, stats::text
            FROM gameplay_skill_combat_definitions
            WHERE revision = @revision
            ORDER BY skill_id;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplaySkillCombatDefinition(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetFloat(3),
                reader.GetFloat(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                TimeSpan.FromSeconds(
                    decimal.ToDouble(reader.GetDecimal(9))),
                TimeSpan.FromSeconds(
                    decimal.ToDouble(reader.GetDecimal(10))))
            {
                DisplayName = reader.GetString(11),
                BaseName = reader.GetString(12),
                SkillLevel = reader.IsDBNull(13)
                    ? null
                    : reader.GetInt16(13),
                ClassIds = reader.GetFieldValue<short[]>(14),
                PreviousSkillId = reader.IsDBNull(15)
                    ? null
                    : reader.GetInt32(15),
                MinLevel = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                MaxLevel = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                Description = reader.GetString(18),
                StatsJson = reader.GetString(19)
            });
        }

        return values.ToArray();
    }

    private static void ValidateGameplayCounts(
        PublishedGameplayHeader header,
        GameplayContentCatalog content)
    {
        if (content.Maps.Count != header.MapCount ||
            content.AddressPoints.Count != header.AddressPointCount ||
            content.Links.Count != header.LinkCount ||
            content.MonsterTemplates.Count != header.MonsterTemplateCount ||
            content.WorldBosses.Count != header.WorldBossCount ||
            content.PendingWorldBossAreas.Count !=
                header.PendingWorldBossCount ||
            content.Classes.Count != header.ClassCount ||
            content.TalentEffects.Count != header.TalentEffectCount ||
            content.Talents.Count != header.TalentCount ||
            content.SkillCombatDefinitions.Count != header.SkillCount ||
            content.SkillBooks.Count != header.SkillBookCount)
        {
            throw GameplayUnavailable(
                "Published gameplay row counts do not match the revision " +
                "declaration.");
        }
    }

    private static NpgsqlCommand RevisionCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        return command;
    }

    private static WorldContentUnavailableException GameplayUnavailable(
        string message) =>
        new(
            "gameplay",
            WorldContentFailureReason.Invalid,
            message);
}
