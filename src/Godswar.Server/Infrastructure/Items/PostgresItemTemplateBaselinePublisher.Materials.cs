using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private sealed record PublishedMaterialPolicies(
        IReadOnlyList<ForgingMaterialDefinition> Forging,
        IReadOnlyList<GearEnhancementMaterialDefinition> Enhancement,
        IReadOnlyList<AttributeDustDefinition> Dusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> Recipes);

    private static IReadOnlyList<ForgingMaterialDefinition> ReviewedForgingMaterials =>
        ForgingMaterialCatalog.All;

    private static IReadOnlyList<GearEnhancementMaterialDefinition> ReviewedEnhancementMaterials =>
        GearEnhancementMaterialCatalog.All;

    private static IReadOnlyList<AttributeDustDefinition> ReviewedAttributeDusts =>
        GearMentorMaterialCatalog.AttributeDusts;

    private static IReadOnlyList<GearMentorMaterialRecipeDefinition>
        ReviewedMaterialRecipes { get; } =
        [
            new(4234, 4233, GearMentorMaterialRecipeKind.CrystalTransform, 1, 2),
            new(4233, 4232, GearMentorMaterialRecipeKind.CrystalTransform, 1, 2),
            new(4232, 4231, GearMentorMaterialRecipeKind.CrystalTransform, 1, 4),
            new(4231, 4230, GearMentorMaterialRecipeKind.CrystalTransform, 1, 8),
            new(4214, 4213, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1),
            new(4224, 4223, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1),
            new(4216, 4215, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1),
            new(4226, 4225, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1),
            new(4235, 4234, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1)
        ];

    private static async Task InsertMaterialPoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        ItemPolicySnapshot policies,
        CancellationToken cancellationToken)
    {
        var materialRecipes = policies.Recipes.ToDictionary(
            static recipe => recipe.SourceItemId);
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_material_content_definitions (
                revision, item_id, policy_kind, stack_cap, random_value,
                distribution, granted_bound, material, material_level,
                is_piece, attribute_name, attribute_ids, can_enhance,
                source_attribute_level, target_attribute_level,
                target_item_id, recipe_quantity, recipe_kind,
                source_quantity, target_quantity)
            VALUES (
                @revision, @itemId, @policyKind, @stackCap, @randomValue,
                @distribution, @grantedBound, @material, @materialLevel,
                @isPiece, @attributeName, @attributeIds, @canEnhance,
                @sourceAttributeLevel, @targetAttributeLevel,
                @targetItemId, @recipeQuantity, @recipeKind,
                @sourceQuantity, @targetQuantity);
            """, connection, transaction);
        foreach (var value in policies.ForgingMaterials)
        {
            SetMaterialParameters(
                command, materialRecipes, revision, value.ItemId,
                "forging", value.StackCap,
                value.Random, value.Distribution, value.GrantedBound,
                value.Material, checked((short?)value.Level), value.IsPiece,
                null, [], false, null, null, null, null);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var value in policies.EnhancementMaterials)
        {
            SetMaterialParameters(
                command, materialRecipes, revision, value.ItemId,
                EnhancementKind(value.Kind),
                value.StackCap, value.Random, value.Distribution, 0,
                null, null, false, value.AttributeName,
                value.AllowedAttributeIds.ToArray(), value.CanEnhance,
                value.SourceAttributeLevel, value.TargetAttributeLevel,
                null, null);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var value in policies.AttributeDusts)
        {
            SetMaterialParameters(
                command, materialRecipes, revision, value.ItemId,
                "attribute_dust",
                value.StackCap, 0, "50,150", value.GrantedBound,
                null, null, false, null, [], false, null, null,
                checked((int)value.AttributeStoneItemId), value.RecipeQuantity);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void SetMaterialParameters(
        NpgsqlCommand command,
        IReadOnlyDictionary<uint, GearMentorMaterialRecipeDefinition>
            materialRecipes,
        string revision,
        uint itemId,
        string kind,
        short stackCap,
        int random,
        string distribution,
        short grantedBound,
        string? material,
        short? materialLevel,
        bool isPiece,
        string? attributeName,
        int[] attributeIds,
        bool canEnhance,
        short? sourceLevel,
        short? targetLevel,
        int? targetItemId,
        int? recipeQuantity)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("itemId", checked((int)itemId));
        command.Parameters.AddWithValue("policyKind", kind);
        command.Parameters.AddWithValue("stackCap", stackCap);
        command.Parameters.AddWithValue("randomValue", random);
        command.Parameters.AddWithValue("distribution", distribution);
        command.Parameters.AddWithValue("grantedBound", grantedBound);
        AddNullable(command, "material", NpgsqlDbType.Varchar, material);
        AddNullable(command, "materialLevel", NpgsqlDbType.Smallint, materialLevel);
        command.Parameters.AddWithValue("isPiece", isPiece);
        AddNullable(command, "attributeName", NpgsqlDbType.Varchar, attributeName);
        command.Parameters.Add(new NpgsqlParameter(
            "attributeIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = attributeIds
        });
        command.Parameters.AddWithValue("canEnhance", canEnhance);
        AddNullable(command, "sourceAttributeLevel", NpgsqlDbType.Smallint, sourceLevel);
        AddNullable(command, "targetAttributeLevel", NpgsqlDbType.Smallint, targetLevel);
        materialRecipes.TryGetValue(itemId, out var materialRecipe);
        AddNullable(
            command,
            "targetItemId",
            NpgsqlDbType.Integer,
            materialRecipe is null
                ? targetItemId
                : checked((int)materialRecipe.TargetItemId));
        AddNullable(command, "recipeQuantity", NpgsqlDbType.Integer, recipeQuantity);
        AddNullable(
            command,
            "recipeKind",
            NpgsqlDbType.Varchar,
            materialRecipe is null
                ? null
                : MaterialRecipeKind(materialRecipe.Kind));
        AddNullable(
            command,
            "sourceQuantity",
            NpgsqlDbType.Integer,
            materialRecipe?.SourceQuantity);
        AddNullable(
            command,
            "targetQuantity",
            NpgsqlDbType.Integer,
            materialRecipe?.TargetQuantity);
    }

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type)
        {
            Value = value ?? DBNull.Value
        });

    private static string EnhancementKind(GearEnhancementMaterialKind value) =>
        value switch
        {
            GearEnhancementMaterialKind.AttributeStone => "attribute_stone",
            GearEnhancementMaterialKind.QuartzPlate => "quartz_plate",
            GearEnhancementMaterialKind.FlameSpark => "flame_spark",
            GearEnhancementMaterialKind.WaterGrain => "water_grain",
            _ => throw new InvalidDataException($"Unknown enhancement material kind {value}.")
        };

    private static string MaterialRecipeKind(
        GearMentorMaterialRecipeKind value) =>
        value switch
        {
            GearMentorMaterialRecipeKind.CrystalTransform =>
                "crystal_transform",
            GearMentorMaterialRecipeKind.GemPieceCombination =>
                "gem_piece_combination",
            _ => throw new InvalidDataException(
                $"Unknown Gear Mentor recipe kind {value}.")
        };

    private static async Task<PublishedMaterialPolicies>
        ReadPublishedMaterialPoliciesAsync(
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
            var granted = reader.GetInt16(5);
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
                    itemId, nameKey, displayName, reader.GetString(19),
                    stackCap, reader.GetString(6), reader.GetInt16(7),
                    reader.GetBoolean(8), texture, icon,
                    granted == 0 ? null : (short)1, random, distribution));
            }
            else if (kind == "attribute_dust")
            {
                dusts.Add(new AttributeDustDefinition(
                    itemId, nameKey, displayName,
                    checked((uint)reader.GetInt32(14)), texture, icon,
                    stackCap, reader.GetInt32(15), granted));
            }
            else
            {
                enhancement.Add(new GearEnhancementMaterialDefinition(
                    itemId, nameKey, displayName, ParseEnhancementKind(kind),
                    texture, icon, stackCap, random, distribution,
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetFieldValue<int[]>(10), reader.GetBoolean(11),
                    reader.IsDBNull(12) ? null : reader.GetInt16(12),
                    reader.IsDBNull(13) ? null : reader.GetInt16(13)));
            }
        }
        return new PublishedMaterialPolicies(
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
