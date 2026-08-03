using System.Data;
using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateCatalogLoader
{
    public static async Task<PinnedItemTemplateCatalog> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
        await using (var readOnly = new NpgsqlCommand(
                         "SET TRANSACTION READ ONLY;",
                         connection,
                         transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        var publication = await ReadPublicationAsync(
            connection,
            transaction,
            cancellationToken);
        var definitions = await ReadDefinitionsAsync(
            connection,
            transaction,
            publication.Revision,
            cancellationToken);
        var policies = await ReadPoliciesAsync(
            connection,
            transaction,
            publication.Revision,
            cancellationToken);
        var materials = await ReadMaterialPoliciesAsync(
            connection,
            transaction,
            publication.Revision,
            cancellationToken);
        var holySuit = await ReadHolySuitPoliciesAsync(
            connection,
            transaction,
            publication.Revision,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (publication.ManifestVersion != 9 ||
            definitions.Count != publication.EntryCount ||
            policies.Attributes.Count != publication.AttributeCount ||
            policies.EquipmentRanks.Count !=
                publication.EquipmentRankCount ||
            policies.HolySuitEffects.Count !=
                publication.HolySuitEffectCount ||
            materials.Count != publication.MaterialPolicyCount ||
            materials.Recipes.Count != publication.MaterialRecipeCount ||
            holySuit.Tiers.Count != publication.HolySuitTierCount ||
            holySuit.Upgrades.Count != publication.HolySuitUpgradeCount ||
            holySuit.Consumables.Count !=
                publication.HolySuitConsumableCount ||
            publication.HolySuitPolicyCount != 1 ||
            policies.Attributes.Count == 0 ||
            policies.EquipmentRanks.Count == 0 ||
            policies.HolySuitEffects.Count == 0 ||
            materials.Count == 0 ||
            materials.Recipes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Published item-content revision {publication.Revision} " +
                "has an incomplete v9 manifest.");
        }

        return PinnedItemTemplateCatalog.CreateV9(
            publication.Source,
            definitions,
            policies.Attributes,
            policies.EquipmentRanks,
            policies.HolySuitEffects,
            materials.Forging,
            materials.Enhancement,
            materials.Dusts,
            materials.Recipes,
            holySuit.Tiers,
            holySuit.Upgrades,
            holySuit.Consumables,
            holySuit.OperationPolicy,
            publication.Revision);
    }

    private static async Task<(
        string Revision,
        int EntryCount,
        string Source,
        int ManifestVersion,
        int AttributeCount,
        int EquipmentRankCount,
        int HolySuitEffectCount,
        int MaterialPolicyCount,
        int MaterialRecipeCount,
        int HolySuitTierCount,
        int HolySuitUpgradeCount,
        int HolySuitConsumableCount,
        int HolySuitPolicyCount)> ReadPublicationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT publication.revision,
                   revision.entry_count,
                   revision.source,
                   revision.manifest_version,
                   revision.attribute_count,
                   revision.equipment_rank_count,
                   revision.holy_suit_effect_count,
                   revision.material_policy_count,
                   revision.material_recipe_count,
                   revision.holy_suit_tier_count,
                   revision.holy_suit_upgrade_count,
                   revision.holy_suit_consumable_count,
                   revision.holy_suit_policy_count,
                   revision.sealed_at IS NOT NULL
            FROM item_template_content_publication publication
            JOIN item_template_content_revisions revision
              ON revision.revision = publication.revision
            WHERE publication.family = 'items';
            """, connection, transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "No item-template content revision is published.");
        }

        if (!reader.GetBoolean(13))
        {
            throw new InvalidOperationException(
                $"Published item-template revision {reader.GetString(0)} " +
                "is not sealed.");
        }

        return (
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetInt16(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12));
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadDefinitionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var definitions = new List<ItemTemplateDefinition>();
        await using var command = new NpgsqlCommand("""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM item_template_content_definitions
            WHERE revision = @revision
            ORDER BY id;
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add(
                PostgresItemTemplateBaselinePublisher.ReadDefinition(
                    reader));
        }

        return definitions;
    }
}
