using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateCatalogLoader
{
    private sealed record LoadedMaterialPolicies(
        IReadOnlyList<ForgingMaterialDefinition> Forging,
        IReadOnlyList<GearEnhancementMaterialDefinition> Enhancement,
        IReadOnlyList<AttributeDustDefinition> Dusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> Recipes)
    {
        public int Count => Forging.Count + Enhancement.Count + Dusts.Count;
    }

    private static async Task<LoadedMaterialPolicies> ReadMaterialPoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var forging = new List<ForgingMaterialDefinition>();
        var enhancement = new List<GearEnhancementMaterialDefinition>();
        var dusts = new List<AttributeDustDefinition>();
        var recipes = new List<GearMentorMaterialRecipeDefinition>();
        await using var command = new NpgsqlCommand("""
            SELECT policy.item_id, policy.policy_kind, policy.stack_cap,
                   policy.random_value, policy.distribution,
                   policy.granted_bound, policy.material,
                   policy.material_level, policy.is_piece,
                   policy.attribute_name, policy.attribute_ids,
                   policy.can_enhance, policy.source_attribute_level,
                   policy.target_attribute_level, policy.target_item_id,
                   policy.recipe_quantity, policy.recipe_kind,
                   policy.source_quantity, policy.target_quantity,
                   template.kind,
                   template.name_key, template.display_name,
                   template.texture, template.icon
            FROM item_material_content_definitions policy
            JOIN item_template_content_definitions template
              ON template.revision = policy.revision
             AND template.id = policy.item_id
            WHERE policy.revision = @revision
            ORDER BY policy.item_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemId = checked((uint)reader.GetInt32(0));
            var kind = reader.GetString(1);
            var stackCap = reader.GetInt16(2);
            var random = reader.GetInt32(3);
            var distribution = reader.GetString(4);
            var grantedBound = reader.GetInt16(5);
            var nameKey = reader.GetString(20);
            var displayName = reader.GetString(21);
            var texture = reader.GetString(22);
            var icon = reader.GetString(23);
            if (!reader.IsDBNull(16))
            {
                recipes.Add(new GearMentorMaterialRecipeDefinition(
                    itemId,
                    checked((uint)reader.GetInt32(14)),
                    ParseMaterialRecipeKind(reader.GetString(16)),
                    reader.GetInt32(17),
                    reader.GetInt32(18)));
            }
            if (kind == "forging")
            {
                forging.Add(new ForgingMaterialDefinition(
                    itemId,
                    nameKey,
                    displayName,
                    reader.GetString(19),
                    stackCap,
                    reader.GetString(6),
                    reader.GetInt16(7),
                    reader.GetBoolean(8),
                    texture,
                    icon,
                    grantedBound == 0 ? null : (short)1,
                    random,
                    distribution));
                continue;
            }

            if (kind == "attribute_dust")
            {
                dusts.Add(new AttributeDustDefinition(
                    itemId,
                    nameKey,
                    displayName,
                    checked((uint)reader.GetInt32(14)),
                    texture,
                    icon,
                    stackCap,
                    reader.GetInt32(15),
                    grantedBound));
                continue;
            }

            enhancement.Add(new GearEnhancementMaterialDefinition(
                itemId,
                nameKey,
                displayName,
                ParseEnhancementKind(kind),
                texture,
                icon,
                stackCap,
                random,
                distribution,
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetFieldValue<int[]>(10),
                reader.GetBoolean(11),
                reader.IsDBNull(12) ? null : reader.GetInt16(12),
                reader.IsDBNull(13) ? null : reader.GetInt16(13)));
        }
        return new LoadedMaterialPolicies(
            forging,
            enhancement,
            dusts,
            recipes);
    }

    private static GearEnhancementMaterialKind ParseEnhancementKind(string value) =>
        value switch
        {
            "attribute_stone" => GearEnhancementMaterialKind.AttributeStone,
            "quartz_plate" => GearEnhancementMaterialKind.QuartzPlate,
            "flame_spark" => GearEnhancementMaterialKind.FlameSpark,
            "water_grain" => GearEnhancementMaterialKind.WaterGrain,
            _ => throw new InvalidDataException(
                $"Unknown published item-material policy kind '{value}'.")
        };

    private static GearMentorMaterialRecipeKind ParseMaterialRecipeKind(
        string value) =>
        value switch
        {
            "crystal_transform" =>
                GearMentorMaterialRecipeKind.CrystalTransform,
            "gem_piece_combination" =>
                GearMentorMaterialRecipeKind.GemPieceCombination,
            _ => throw new InvalidDataException(
                $"Unknown published Gear Mentor recipe kind '{value}'.")
        };
}
