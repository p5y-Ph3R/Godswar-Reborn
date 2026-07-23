using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private async Task SeedMapTemplatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            INSERT INTO map_templates (
                map_id, scene_key, display_name, client_scene_id, map_mode,
                address_file, music_name, event_scene_key, stats
            )
            VALUES (
                @mapId, @sceneKey, @displayName, @clientSceneId, @mapMode,
                @addressFile, @musicName, @eventSceneKey, @stats
            )
            ON CONFLICT (map_id) DO UPDATE
            SET scene_key = EXCLUDED.scene_key,
                display_name = EXCLUDED.display_name,
                client_scene_id = EXCLUDED.client_scene_id,
                map_mode = EXCLUDED.map_mode,
                address_file = EXCLUDED.address_file,
                music_name = EXCLUDED.music_name,
                event_scene_key = EXCLUDED.event_scene_key,
                stats = EXCLUDED.stats;
            """, connection, transaction))
        {
            foreach (var template in MapTemplateSeeds.Maps)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("mapId", template.MapId);
                command.Parameters.AddWithValue("sceneKey", template.SceneKey);
                command.Parameters.AddWithValue("displayName", template.DisplayName);
                AddNullableIntegerParameter(command, "clientSceneId", template.ClientSceneId);
                AddNullableSmallintParameter(command, "mapMode", template.MapMode);
                command.Parameters.AddWithValue("addressFile", template.AddressFile);
                command.Parameters.AddWithValue("musicName", template.MusicName);
                command.Parameters.AddWithValue("eventSceneKey", template.EventSceneKey);
                command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Jsonb)
                {
                    Value = template.StatsJson
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO map_safe_areas (map_id, area_index, x1, z1, x2, z2, attribute)
            VALUES (@mapId, @areaIndex, @x1, @z1, @x2, @z2, @attribute)
            ON CONFLICT (map_id, area_index) DO UPDATE
            SET x1 = EXCLUDED.x1,
                z1 = EXCLUDED.z1,
                x2 = EXCLUDED.x2,
                z2 = EXCLUDED.z2,
                attribute = EXCLUDED.attribute;
            """, connection, transaction))
        {
            foreach (var area in MapTemplateSeeds.SafeAreas)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("mapId", area.MapId);
                command.Parameters.AddWithValue("areaIndex", area.AreaIndex);
                command.Parameters.AddWithValue("x1", area.X1);
                command.Parameters.AddWithValue("z1", area.Z1);
                command.Parameters.AddWithValue("x2", area.X2);
                command.Parameters.AddWithValue("z2", area.Z2);
                AddNullableSmallintParameter(command, "attribute", area.Attribute);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO map_address_points (
                map_id, group_index, point_index, group_name, name, pos_x, pos_z, source
            )
            VALUES (
                @mapId, @groupIndex, @pointIndex, @groupName, @name, @posX, @posZ, @source
            )
            ON CONFLICT (map_id, group_index, point_index) DO UPDATE
            SET group_name = EXCLUDED.group_name,
                name = EXCLUDED.name,
                pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                source = EXCLUDED.source;
            """, connection, transaction))
        {
            foreach (var point in MapTemplateSeeds.AddressPoints)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("mapId", point.MapId);
                command.Parameters.AddWithValue("groupIndex", point.GroupIndex);
                command.Parameters.AddWithValue("pointIndex", point.PointIndex);
                command.Parameters.AddWithValue("groupName", point.GroupName);
                command.Parameters.AddWithValue("name", point.Name);
                command.Parameters.AddWithValue("posX", point.X);
                command.Parameters.AddWithValue("posZ", point.Z);
                command.Parameters.AddWithValue("source", point.Source);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO map_links (map_id, link_index, target_map_id, pos_x, pos_z, source)
            VALUES (@mapId, @linkIndex, @targetMapId, @posX, @posZ, @source)
            ON CONFLICT (map_id, link_index, target_map_id) DO UPDATE
            SET pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                source = EXCLUDED.source;
            """, connection, transaction))
        {
            foreach (var link in MapTemplateSeeds.Links)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("mapId", link.MapId);
                command.Parameters.AddWithValue("linkIndex", link.LinkIndex);
                command.Parameters.AddWithValue("targetMapId", link.TargetMapId);
                command.Parameters.AddWithValue("posX", link.X);
                command.Parameters.AddWithValue("posZ", link.Z);
                command.Parameters.AddWithValue("source", link.Source);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO map_routes (camp, route_index, map_ids, source)
            VALUES (@camp, @routeIndex, @mapIds, @source)
            ON CONFLICT (camp, route_index) DO UPDATE
            SET map_ids = EXCLUDED.map_ids,
                source = EXCLUDED.source;
            """, connection, transaction))
        {
            foreach (var route in MapTemplateSeeds.Routes)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("camp", route.Camp);
                command.Parameters.AddWithValue("routeIndex", route.RouteIndex);
                command.Parameters.Add(new NpgsqlParameter("mapIds", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
                {
                    Value = route.MapIds
                });
                command.Parameters.AddWithValue("source", route.Source);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SeedMonsterTemplatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            INSERT INTO monster_templates (
                source_key, source_kind, source_map_id, scene_key, template_key, display_name,
                rank, is_boss, is_elite, is_pet, attack_type, model_file, texture_file,
                scale, collision_range, name_height, stats
            )
            VALUES (
                @sourceKey, @sourceKind, @sourceMapId, @sceneKey, @templateKey, @displayName,
                @rank, @isBoss, @isElite, @isPet, @attackType, @modelFile, @textureFile,
                @scale, @collisionRange, @nameHeight, @stats
            )
            ON CONFLICT (source_key, template_key) DO UPDATE
            SET source_kind = EXCLUDED.source_kind,
                source_map_id = EXCLUDED.source_map_id,
                scene_key = EXCLUDED.scene_key,
                display_name = EXCLUDED.display_name,
                rank = EXCLUDED.rank,
                is_boss = EXCLUDED.is_boss,
                is_elite = EXCLUDED.is_elite,
                is_pet = EXCLUDED.is_pet,
                attack_type = EXCLUDED.attack_type,
                model_file = EXCLUDED.model_file,
                texture_file = EXCLUDED.texture_file,
                scale = EXCLUDED.scale,
                collision_range = EXCLUDED.collision_range,
                name_height = EXCLUDED.name_height,
                stats = EXCLUDED.stats;
            """, connection, transaction))
        {
            foreach (var template in MonsterTemplateSeeds.Monsters)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("sourceKey", template.SourceKey);
                command.Parameters.AddWithValue("sourceKind", template.SourceKind);
                AddNullableSmallintParameter(command, "sourceMapId", template.SourceMapId);
                command.Parameters.AddWithValue("sceneKey", template.SceneKey);
                command.Parameters.AddWithValue("templateKey", template.TemplateKey);
                command.Parameters.AddWithValue("displayName", template.DisplayName);
                command.Parameters.AddWithValue("rank", template.Rank);
                command.Parameters.AddWithValue("isBoss", template.IsBoss);
                command.Parameters.AddWithValue("isElite", template.IsElite);
                command.Parameters.AddWithValue("isPet", template.IsPet);
                AddNullableSmallintParameter(command, "attackType", template.AttackType);
                command.Parameters.AddWithValue("modelFile", template.ModelFile);
                command.Parameters.AddWithValue("textureFile", template.TextureFile);
                AddNullableRealParameter(command, "scale", template.Scale);
                AddNullableRealParameter(command, "collisionRange", template.CollisionRange);
                AddNullableRealParameter(command, "nameHeight", template.NameHeight);
                command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Jsonb)
                {
                    Value = template.StatsJson
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO quest_monster_references (
                quest_id, monster_name, map_id, pos_x, pos_z,
                min_level, max_level, faction, pet_group, source
            )
            VALUES (
                @questId, @monsterName, @mapId, @posX, @posZ,
                @minLevel, @maxLevel, @faction, @petGroup, @source
            )
            ON CONFLICT (quest_id) DO UPDATE
            SET monster_name = EXCLUDED.monster_name,
                map_id = EXCLUDED.map_id,
                pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                min_level = EXCLUDED.min_level,
                max_level = EXCLUDED.max_level,
                faction = EXCLUDED.faction,
                pet_group = EXCLUDED.pet_group,
                source = EXCLUDED.source;
            """, connection, transaction))
        {
            foreach (var reference in MonsterTemplateSeeds.QuestReferences)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("questId", reference.QuestId);
                command.Parameters.AddWithValue("monsterName", reference.MonsterName);
                AddNullableSmallintParameter(command, "mapId", reference.MapId);
                AddNullableRealParameter(command, "posX", reference.X);
                AddNullableRealParameter(command, "posZ", reference.Z);
                AddNullableIntegerParameter(command, "minLevel", reference.MinLevel);
                AddNullableIntegerParameter(command, "maxLevel", reference.MaxLevel);
                AddNullableSmallintParameter(command, "faction", reference.Faction);
                AddNullableSmallintParameter(command, "petGroup", reference.PetGroup);
                command.Parameters.AddWithValue("source", reference.Source);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SeedWorldBossAreasAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var disable = new NpgsqlCommand(
                         "UPDATE world_boss_areas SET enabled = false;",
                         connection,
                         transaction))
        {
            await disable.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO world_boss_areas (
                map_id,
                boss_template_key,
                boss_display_name,
                bonus_basis_points,
                respawn_interval_seconds,
                enabled
            )
            VALUES (@mapId, @templateKey, @displayName, 2500, @respawnSeconds, true)
            ON CONFLICT (map_id) DO UPDATE
            SET boss_template_key = EXCLUDED.boss_template_key,
                boss_display_name = EXCLUDED.boss_display_name,
                bonus_basis_points = EXCLUDED.bonus_basis_points,
                respawn_interval_seconds = EXCLUDED.respawn_interval_seconds,
                enabled = true;
            """, connection, transaction))
        {
            var respawnSeconds = checked((int)WorldBossCatalog.Default.RespawnInterval.TotalSeconds);
            foreach (var definition in WorldBossCatalog.Default.Definitions)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("mapId", definition.MapId);
                command.Parameters.AddWithValue("templateKey", definition.TemplateKey);
                command.Parameters.AddWithValue("displayName", definition.DisplayName);
                command.Parameters.AddWithValue("respawnSeconds", respawnSeconds);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

}
