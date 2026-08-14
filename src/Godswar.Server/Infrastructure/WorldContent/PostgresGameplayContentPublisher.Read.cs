using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresGameplayContentPublisher
{
    private static async Task<GameplayContentCatalog> ReadSourceContentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var maps = await ReadMapsAsync(
            connection,
            transaction,
            cancellationToken);
        var expectedMapIds = Enumerable.Range(0, 70)
            .Concat(Enumerable.Range(200, 11))
            .Select(static value => (short)value)
            .ToHashSet();
        if (!maps.Select(static value => value.MapId)
                .ToHashSet()
                .SetEquals(expectedMapIds))
        {
            throw new InvalidDataException(
                "The reviewed gameplay source must contain maps 0-69 and " +
                "200-210 exactly.");
        }

        return new GameplayContentCatalog(
            maps,
            await ReadAddressPointsAsync(
                connection,
                transaction,
                cancellationToken),
            await ReadLinksAsync(
                connection,
                transaction,
                cancellationToken),
            await ReadMonsterTemplatesAsync(
                connection,
                transaction,
                cancellationToken),
            await ReadWorldBossesAsync(
                connection,
                transaction,
                cancellationToken),
            await ReadPendingWorldBossesAsync(
                connection,
                transaction,
                cancellationToken),
            await ReadSkillsAsync(
                connection,
                transaction,
                cancellationToken))
        {
            Classes = await ReadClassesAsync(
                connection,
                transaction,
                cancellationToken),
            TalentEffects = await ReadTalentEffectsAsync(
                connection,
                transaction,
                cancellationToken),
            Talents = await ReadTalentsAsync(
                connection,
                transaction,
                cancellationToken),
            SkillBooks = await ReadSkillBooksAsync(
                connection,
                transaction,
                cancellationToken)
        };
    }

    private static async Task<GameplayMapDefinition[]> ReadMapsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var values = new List<GameplayMapDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, scene_key, display_name, client_scene_id, map_mode
            FROM map_templates
            ORDER BY map_id;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayMapDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt16(4)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayMapAddressPointDefinition[]>
        ReadAddressPointsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayMapAddressPointDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, group_index, point_index, group_name, name,
                   pos_x, pos_z, source
            FROM map_address_points
            ORDER BY map_id, group_index, point_index;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayMapAddressPointDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFloat(5),
                reader.GetFloat(6),
                reader.GetString(7)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayMapLinkDefinition[]> ReadLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var values = new List<GameplayMapLinkDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, link_index, target_map_id, pos_x, pos_z, source,
                   confidence, activation, note
            FROM map_links
            ORDER BY map_id, link_index, target_map_id;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplayMapLinkDefinition(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetFloat(3),
                reader.GetFloat(4),
                reader.GetString(5),
                GameplayContentDatabaseValues.ParseConfidence(
                    reader.GetString(6)),
                GameplayContentDatabaseValues.ParseActivation(
                    reader.GetString(7)),
                reader.GetString(8)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayMonsterTemplateDefinition[]>
        ReadMonsterTemplatesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayMonsterTemplateDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT source_key, source_kind, source_map_id, scene_key,
                   template_key, display_name, rank, is_boss, is_elite,
                   is_pet, attack_type, collision_range
            FROM monster_templates
            ORDER BY source_key, template_key;
            """,
            connection,
            transaction);
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
                reader.IsDBNull(10) ? null : reader.GetInt16(10),
                reader.IsDBNull(11) ? null : reader.GetFloat(11)));
        }

        return values.ToArray();
    }

    private static async Task<GameplayWorldBossDefinition[]>
        ReadWorldBossesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayWorldBossDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT area.map_id, map.scene_key, area.boss_template_key,
                   area.boss_display_name, area.bonus_basis_points,
                   area.respawn_interval_seconds
            FROM world_boss_areas area
            JOIN map_templates map ON map.map_id = area.map_id
            WHERE area.enabled
            ORDER BY area.map_id;
            """,
            connection,
            transaction);
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
        ReadPendingWorldBossesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplayPendingWorldBossArea>();
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, scene_key, reason
            FROM pending_world_boss_areas
            ORDER BY map_id;
            """,
            connection,
            transaction);
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
        ReadSkillsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var values = new List<GameplaySkillCombatDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT skill_id, target, affect_obj, distance, effect_range,
                   property, mp, power1, power2, intonate_time, cooling_time,
                   display_name, base_name, skill_level, class_ids,
                   previous_skill_id, min_level, max_level, description,
                   stats::text
            FROM skill_templates
            ORDER BY skill_id;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new GameplaySkillCombatDefinition(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                decimal.ToSingle(reader.GetDecimal(3)),
                decimal.ToSingle(reader.GetDecimal(4)),
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

}
