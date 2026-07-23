using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private async Task SeedSkillTalentTemplatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            INSERT INTO class_templates (id, name, display_name, source)
            VALUES (@id, @name, @displayName, @source)
            ON CONFLICT (id) DO UPDATE
            SET name = EXCLUDED.name,
                display_name = EXCLUDED.display_name,
                source = EXCLUDED.source;
            """, connection, transaction))
        {
            foreach (var template in SkillTalentSeeds.Classes)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("id", template.Id);
                command.Parameters.AddWithValue("name", template.Name);
                command.Parameters.AddWithValue("displayName", template.DisplayName);
                command.Parameters.AddWithValue("source", template.Source);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO talent_effect_templates (id, key, display_name, percent)
            VALUES (@id, @key, @displayName, @percent)
            ON CONFLICT (id) DO UPDATE
            SET key = EXCLUDED.key,
                display_name = EXCLUDED.display_name,
                percent = EXCLUDED.percent;
            """, connection, transaction))
        {
            foreach (var template in SkillTalentSeeds.TalentEffects)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("id", template.Id);
                command.Parameters.AddWithValue("key", template.Key);
                command.Parameters.AddWithValue("displayName", template.DisplayName);
                command.Parameters.AddWithValue("percent", template.Percent);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO talent_templates (
                id, class_id, tree_order, name, prefix_id, required_prefix_rank,
                required_total_rank, equip_request, effect_type, effect_id, effect_value,
                is_percent, icon_x, icon_y, icon_width, icon_height, stats
            )
            VALUES (
                @id, @classId, @treeOrder, @name, @prefixId, @requiredPrefixRank,
                @requiredTotalRank, @equipRequest, @effectType, @effectId, @effectValue,
                @isPercent, @iconX, @iconY, @iconWidth, @iconHeight, @stats
            )
            ON CONFLICT (id) DO UPDATE
            SET class_id = EXCLUDED.class_id,
                tree_order = EXCLUDED.tree_order,
                name = EXCLUDED.name,
                prefix_id = EXCLUDED.prefix_id,
                required_prefix_rank = EXCLUDED.required_prefix_rank,
                required_total_rank = EXCLUDED.required_total_rank,
                equip_request = EXCLUDED.equip_request,
                effect_type = EXCLUDED.effect_type,
                effect_id = EXCLUDED.effect_id,
                effect_value = EXCLUDED.effect_value,
                is_percent = EXCLUDED.is_percent,
                icon_x = EXCLUDED.icon_x,
                icon_y = EXCLUDED.icon_y,
                icon_width = EXCLUDED.icon_width,
                icon_height = EXCLUDED.icon_height,
                stats = EXCLUDED.stats;
            """, connection, transaction))
        {
            foreach (var template in SkillTalentSeeds.Talents)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("id", template.Id);
                command.Parameters.AddWithValue("classId", template.ClassId);
                command.Parameters.AddWithValue("treeOrder", template.TreeOrder);
                command.Parameters.AddWithValue("name", template.Name);
                command.Parameters.AddWithValue("prefixId", template.PrefixId);
                command.Parameters.AddWithValue("requiredPrefixRank", template.RequiredPrefixRank);
                command.Parameters.AddWithValue("requiredTotalRank", template.RequiredTotalRank);
                command.Parameters.AddWithValue("equipRequest", template.EquipRequest);
                command.Parameters.AddWithValue("effectType", template.EffectType);
                command.Parameters.AddWithValue("effectId", template.EffectId);
                command.Parameters.Add(new NpgsqlParameter("effectValue", NpgsqlDbType.Numeric)
                {
                    Value = template.EffectValue
                });
                command.Parameters.AddWithValue("isPercent", template.IsPercent);
                command.Parameters.AddWithValue("iconX", template.IconX);
                command.Parameters.AddWithValue("iconY", template.IconY);
                command.Parameters.AddWithValue("iconWidth", template.IconWidth);
                command.Parameters.AddWithValue("iconHeight", template.IconHeight);
                command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Jsonb)
                {
                    Value = template.StatsJson
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO skill_templates (
                skill_id, display_name, base_name, skill_level, class_ids, previous_skill_id,
                min_level, max_level, description, target, affect_obj, distance, effect_range,
                property, mp, power1, power2, stats
            )
            VALUES (
                @skillId, @displayName, @baseName, @skillLevel, @classIds, @previousSkillId,
                @minLevel, @maxLevel, @description, @target, @affectObj, @distance, @effectRange,
                @property, @mp, @power1, @power2, @stats
            )
            ON CONFLICT (skill_id) DO UPDATE
            SET display_name = EXCLUDED.display_name,
                base_name = EXCLUDED.base_name,
                skill_level = EXCLUDED.skill_level,
                class_ids = EXCLUDED.class_ids,
                previous_skill_id = EXCLUDED.previous_skill_id,
                min_level = EXCLUDED.min_level,
                max_level = EXCLUDED.max_level,
                description = EXCLUDED.description,
                target = EXCLUDED.target,
                affect_obj = EXCLUDED.affect_obj,
                distance = EXCLUDED.distance,
                effect_range = EXCLUDED.effect_range,
                property = EXCLUDED.property,
                mp = EXCLUDED.mp,
                power1 = EXCLUDED.power1,
                power2 = EXCLUDED.power2,
                stats = EXCLUDED.stats;
            """, connection, transaction))
        {
            foreach (var template in SkillTalentSeeds.Skills)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("skillId", template.SkillId);
                command.Parameters.AddWithValue("displayName", template.DisplayName);
                command.Parameters.AddWithValue("baseName", template.BaseName);
                AddNullableSmallintParameter(command, "skillLevel", template.SkillLevel);
                command.Parameters.Add(new NpgsqlParameter("classIds", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
                {
                    Value = template.ClassIds
                });
                AddNullableIntegerParameter(command, "previousSkillId", template.PreviousSkillId);
                AddNullableIntegerParameter(command, "minLevel", template.MinLevel);
                AddNullableIntegerParameter(command, "maxLevel", template.MaxLevel);
                command.Parameters.AddWithValue("description", template.Description);
                command.Parameters.AddWithValue("target", template.Target);
                command.Parameters.AddWithValue("affectObj", template.AffectObj);
                command.Parameters.AddWithValue("distance", template.Distance);
                command.Parameters.AddWithValue("effectRange", template.Range);
                command.Parameters.AddWithValue("property", template.Property);
                command.Parameters.AddWithValue("mp", template.Mp);
                command.Parameters.AddWithValue("power1", template.Power1);
                command.Parameters.AddWithValue("power2", template.Power2);
                command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Jsonb)
                {
                    Value = template.StatsJson
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO skill_book_templates (
                item_id, name_key, display_name, skill_id, base_name, skill_level,
                class_ids, min_level, max_level, previous_skill_id, stats
            )
            VALUES (
                @itemId, @nameKey, @displayName, @skillId, @baseName, @skillLevel,
                @classIds, @minLevel, @maxLevel, @previousSkillId, @stats
            )
            ON CONFLICT (item_id) DO UPDATE
            SET name_key = EXCLUDED.name_key,
                display_name = EXCLUDED.display_name,
                skill_id = EXCLUDED.skill_id,
                base_name = EXCLUDED.base_name,
                skill_level = EXCLUDED.skill_level,
                class_ids = EXCLUDED.class_ids,
                min_level = EXCLUDED.min_level,
                max_level = EXCLUDED.max_level,
                previous_skill_id = EXCLUDED.previous_skill_id,
                stats = EXCLUDED.stats;
            """, connection, transaction))
        {
            foreach (var template in SkillTalentSeeds.SkillBooks)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("itemId", template.ItemId);
                command.Parameters.AddWithValue("nameKey", template.NameKey);
                command.Parameters.AddWithValue("displayName", template.DisplayName);
                command.Parameters.AddWithValue("skillId", template.SkillId);
                command.Parameters.AddWithValue("baseName", template.BaseName);
                AddNullableSmallintParameter(command, "skillLevel", template.SkillLevel);
                command.Parameters.Add(new NpgsqlParameter("classIds", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
                {
                    Value = template.ClassIds
                });
                AddNullableIntegerParameter(command, "minLevel", template.MinLevel);
                AddNullableIntegerParameter(command, "maxLevel", template.MaxLevel);
                AddNullableIntegerParameter(command, "previousSkillId", template.PreviousSkillId);
                command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Jsonb)
                {
                    Value = template.StatsJson
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SeedNpcTemplatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            INSERT INTO npc_text_templates (npc_key, scene_key, display_name, description)
            VALUES (@npcKey, @sceneKey, @displayName, @description)
            ON CONFLICT (npc_key) DO UPDATE
            SET scene_key = EXCLUDED.scene_key,
                display_name = EXCLUDED.display_name,
                description = EXCLUDED.description;
            """, connection, transaction))
        {
            foreach (var template in NpcTemplateSeeds.Texts)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("npcKey", template.NpcKey);
                command.Parameters.AddWithValue("sceneKey", template.SceneKey);
                command.Parameters.AddWithValue("displayName", template.DisplayName);
                command.Parameters.AddWithValue("description", template.Description);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO npc_appearance_templates (template_key, npc_key, scene_key, internal_name, sex, stats)
            VALUES (@templateKey, @npcKey, @sceneKey, @internalName, @sex, @stats)
            ON CONFLICT (template_key) DO UPDATE
            SET npc_key = EXCLUDED.npc_key,
                scene_key = EXCLUDED.scene_key,
                internal_name = EXCLUDED.internal_name,
                sex = EXCLUDED.sex,
                stats = EXCLUDED.stats;
            """, connection, transaction))
        {
            foreach (var template in NpcTemplateSeeds.Appearances)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("templateKey", template.TemplateKey);
                command.Parameters.AddWithValue("npcKey", template.NpcKey);
                command.Parameters.AddWithValue("sceneKey", template.SceneKey);
                command.Parameters.AddWithValue("internalName", template.InternalName);
                AddNullableSmallintParameter(command, "sex", template.Sex);
                command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Jsonb)
                {
                    Value = template.StatsJson
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO npc_spawn_references (quest_id, role, npc_key, map_id, pos_x, pos_z, source)
            VALUES (@questId, @role, @npcKey, @mapId, @posX, @posZ, @source)
            ON CONFLICT (quest_id, role, npc_key) DO UPDATE
            SET map_id = EXCLUDED.map_id,
                pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                source = EXCLUDED.source;
            """, connection, transaction))
        {
            foreach (var template in NpcTemplateSeeds.SpawnReferences)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("questId", template.QuestId);
                command.Parameters.AddWithValue("role", template.Role);
                command.Parameters.AddWithValue("npcKey", template.NpcKey);
                command.Parameters.AddWithValue("mapId", template.MapId);
                command.Parameters.AddWithValue("posX", template.X);
                command.Parameters.AddWithValue("posZ", template.Z);
                command.Parameters.AddWithValue("source", template.Source);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO npc_function_templates (function_flag, function_key, display_name, script_file, source)
            VALUES (@functionFlag, @functionKey, @displayName, @scriptFile, @source)
            ON CONFLICT (function_flag) DO UPDATE
            SET function_key = EXCLUDED.function_key,
                display_name = EXCLUDED.display_name,
                script_file = EXCLUDED.script_file,
                source = EXCLUDED.source;
            """, connection, transaction))
        {
            foreach (var template in NpcTemplateSeeds.Functions)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("functionFlag", template.FunctionFlag);
                command.Parameters.AddWithValue("functionKey", template.FunctionKey);
                command.Parameters.AddWithValue("displayName", template.DisplayName);
                command.Parameters.AddWithValue("scriptFile", template.ScriptFile);
                command.Parameters.AddWithValue("source", template.Source);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO npc_dialog_templates (
                script_key, function_name, dialog_index, sub_id, element_kind, text_key, text, stats
            )
            VALUES (
                @scriptKey, @functionName, @dialogIndex, @subId, @elementKind, @textKey, @text, @stats
            )
            ON CONFLICT (script_key, function_name, dialog_index, sub_id, element_kind, text_key) DO UPDATE
            SET text = EXCLUDED.text,
                stats = EXCLUDED.stats;
            """, connection, transaction))
        {
            foreach (var template in NpcTemplateSeeds.Dialogs)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("scriptKey", template.ScriptKey);
                command.Parameters.AddWithValue("functionName", template.FunctionName);
                command.Parameters.AddWithValue("dialogIndex", template.DialogIndex);
                command.Parameters.AddWithValue("subId", template.SubId);
                command.Parameters.AddWithValue("elementKind", template.ElementKind);
                command.Parameters.AddWithValue("textKey", template.TextKey);
                command.Parameters.AddWithValue("text", template.Text);
                command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Jsonb)
                {
                    Value = template.StatsJson
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

}
