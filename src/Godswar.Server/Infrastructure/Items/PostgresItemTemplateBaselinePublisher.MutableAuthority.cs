using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task UpsertReviewedBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ValidateReviewedSkillBookConflicts();
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_templates (
                id, kind, name_key, display_name, equipment_slot, class_ids,
                min_level, max_level, hand, skill_flag, texture, icon, stats
            )
            VALUES (
                @id, @kind, @nameKey, @displayName, @equipmentSlot, @classIds,
                @minLevel, @maxLevel, @hand, @skillFlag, @texture, @icon, @stats
            )
            ON CONFLICT (id) DO UPDATE
            SET kind = EXCLUDED.kind,
                name_key = EXCLUDED.name_key,
                display_name = EXCLUDED.display_name,
                equipment_slot = EXCLUDED.equipment_slot,
                class_ids = EXCLUDED.class_ids,
                min_level = EXCLUDED.min_level,
                max_level = EXCLUDED.max_level,
                hand = EXCLUDED.hand,
                skill_flag = EXCLUDED.skill_flag,
                texture = EXCLUDED.texture,
                icon = EXCLUDED.icon,
                stats = EXCLUDED.stats;
            """, connection, transaction);

        var templates = ItemTemplateSeeds.All
            .Concat(SkillTalentSeeds.SkillBooks.Select(
                static skillBook => skillBook.ToItemTemplateSeed()))
            .Concat(ForgingMaterialCatalog.All.Select(
                static material => material.ToItemTemplateSeed()))
            .Concat(GearEnhancementMaterialCatalog.All.Select(
                static material => material.ToItemTemplateSeed()))
            .Concat(GearMentorMaterialCatalog.AttributeDusts.Select(
                static material => material.ToItemTemplateSeed()))
            .Concat(HolySuitContentBaseline.ItemTemplates);
        foreach (var template in templates)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("id", template.Id);
            command.Parameters.AddWithValue("kind", template.Kind);
            command.Parameters.AddWithValue("nameKey", template.NameKey);
            command.Parameters.AddWithValue(
                "displayName",
                template.DisplayName);
            command.Parameters.AddWithValue(
                "equipmentSlot",
                template.EquipmentSlot);
            command.Parameters.Add(new NpgsqlParameter(
                "classIds",
                NpgsqlDbType.Array | NpgsqlDbType.Smallint)
            {
                Value = template.ClassIds
            });
            command.Parameters.Add(new NpgsqlParameter(
                "minLevel",
                NpgsqlDbType.Integer)
            {
                Value = template.MinLevel is null
                    ? DBNull.Value
                    : template.MinLevel
            });
            command.Parameters.Add(new NpgsqlParameter(
                "maxLevel",
                NpgsqlDbType.Integer)
            {
                Value = template.MaxLevel is null
                    ? DBNull.Value
                    : template.MaxLevel
            });
            command.Parameters.Add(new NpgsqlParameter(
                "hand",
                NpgsqlDbType.Smallint)
            {
                Value = template.Hand is null
                    ? DBNull.Value
                    : template.Hand
            });
            command.Parameters.Add(new NpgsqlParameter(
                "skillFlag",
                NpgsqlDbType.Integer)
            {
                Value = template.SkillFlag is null
                    ? DBNull.Value
                    : template.SkillFlag
            });
            command.Parameters.AddWithValue("texture", template.Texture);
            command.Parameters.AddWithValue("icon", template.Icon);
            command.Parameters.Add(new NpgsqlParameter(
                "stats",
                NpgsqlDbType.Jsonb)
            {
                Value = template.StatsJson
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadAuthoritativeDefinitionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var definitions = new List<ItemTemplateDefinition>();
        await using var command = new NpgsqlCommand("""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM item_templates
            ORDER BY id;
            """, connection, transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add(ReadDefinition(reader));
        }

        if (definitions.Count == 0)
        {
            throw new InvalidOperationException(
                "The authoritative item-template table is empty.");
        }

        return definitions;
    }

    internal static ItemTemplateDefinition ReadDefinition(
        NpgsqlDataReader reader) => new(
            checked((uint)reader.GetInt32(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt16(4),
            reader.GetFieldValue<short[]>(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt16(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12));
}
