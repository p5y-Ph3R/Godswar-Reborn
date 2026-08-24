using System.Data;
using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal sealed record ItemTemplatePublicationResult(
    string Revision,
    int EntryCount,
    bool Created);

/// <summary>
/// The only production boundary allowed to consume compiled item seeds. It
/// publishes the reviewed baseline into PostgreSQL and snapshots the complete
/// authoritative table under an immutable content revision.
/// </summary>
internal static partial class PostgresItemTemplateBaselinePublisher
{
    private const long PublicationLockId = 0x4954454D53434F4E;
    private const string PublicationSource =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v4+wh-v1";

    public static async Task<ItemTemplatePublicationResult>
        EnsurePublishedAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
        await AcquirePublicationLockAsync(
            connection,
            transaction,
            cancellationToken);
        var existing = await TryReadPublishedRevisionAsync(
            connection,
            transaction,
            cancellationToken);
        if (existing is { ManifestVersion: 9 })
        {
            await VerifyPublishedV9ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            var publishedHolySuit =
                await ReadPublishedHolySuitPoliciesAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            var hasClassSuitItems =
                await PublishedClassSuitItemsAreCompleteAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            var hasElementalContent =
                await PublishedElementalContentIsCompleteAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            var hasSocketSpells =
                await PublishedSocketSpellItemsAreCompleteAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            var hasHolyStoneMaterials =
                await PublishedHolyStoneMaterialsAreCompleteAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            var hasPetItems =
                await PublishedPetItemsAreCompleteAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            var hasWarehouseItems =
                await PublishedWarehouseItemsAreCompleteAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            var hasMountSpeedProfile =
                await PublishedMountSpeedProfileIsCurrentAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            if (hasClassSuitItems &&
                hasElementalContent &&
                hasSocketSpells &&
                hasHolyStoneMaterials &&
                hasPetItems &&
                hasWarehouseItems &&
                hasMountSpeedProfile &&
                publishedHolySuit.OperationPolicy.Equals(
                    ReviewedHolySuitPolicy.OperationPolicy))
            {
                await EnsureHolySuitMutableTemplateCompatibilityAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
                await EnsureClassSuitMutableTemplateCompatibilityAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
                await EnsureElementalMutableTemplateCompatibilityAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    upgradeFromV8Revision: null,
                    cancellationToken);
                await EnsureHolyStoneMutableTemplateCompatibilityAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
                await EnsurePetItemMutableTemplateCompatibilityAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
                await EnsureWarehouseMutableCompatibilityAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ItemTemplatePublicationResult(
                    existing.Revision,
                    existing.EntryCount,
                    Created: false);
            }
        }

        var snapshot = await PrepareV9PublicationAsync(
            connection,
            transaction,
            existing,
            cancellationToken);
        var definitions = snapshot.Definitions;
        var policies = snapshot.Policies;
        var holySuit = snapshot.HolySuit;
        var revision = ItemTemplateContentRevisionHasher.ComputeV6(
            definitions,
            policies.Attributes,
            policies.EquipmentRanks,
            policies.HolySuitEffects,
            policies.ForgingMaterials,
            policies.EnhancementMaterials,
            policies.AttributeDusts,
            policies.Recipes,
            holySuit.Tiers,
            holySuit.Upgrades,
            holySuit.Consumables,
            holySuit.OperationPolicy);

        var created = await InsertRevisionAsync(
            connection,
            transaction,
            revision,
            definitions.Count,
            policies,
            holySuit,
            cancellationToken);
        if (created)
        {
            await InsertDefinitionsAsync(
                connection,
                transaction,
                revision,
                definitions,
                cancellationToken);
            await InsertPoliciesAsync(
                connection,
                transaction,
                revision,
                policies,
                holySuit,
                cancellationToken);
        }
        await VerifyDefinitionIntegrityAsync(
            connection,
            transaction,
            revision,
            definitions,
            policies,
            holySuit,
            cancellationToken);
        await EnsureHolySuitMutableTemplateCompatibilityAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        await EnsureClassSuitMutableTemplateCompatibilityAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        await EnsureElementalMutableTemplateCompatibilityAsync(
            connection,
            transaction,
            revision,
            existing is { ManifestVersion: 8 }
                ? existing.Revision
                : null,
            cancellationToken);
        await EnsureHolyStoneMutableTemplateCompatibilityAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        await EnsurePetItemMutableTemplateCompatibilityAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        await EnsureWarehouseMutableCompatibilityAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        await PublishRevisionAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ItemTemplatePublicationResult(
            revision,
            definitions.Count,
            created);
    }

    private static async Task<PublishedItemRevisionState?>
        TryReadPublishedRevisionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand("""
                         SELECT publication.revision, revision.entry_count,
                                revision.source, revision.manifest_version,
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
                         """, connection, transaction))
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetBoolean(13))
                {
                    throw new InvalidOperationException(
                        $"Published item-template revision {reader.GetString(0)} is not sealed.");
                }

                return new PublishedItemRevisionState(
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
        }

        return null;
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadPublishedDefinitionsAsync(
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
            definitions.Add(ReadDefinition(reader));
        }

        return definitions;
    }

    private static async Task AcquirePublicationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lockId);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lockId", PublicationLockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        int entryCount,
        ItemPolicySnapshot policies,
        HolySuitPolicySnapshot holySuit,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_revisions (
                revision, entry_count, source, manifest_version,
                attribute_count, equipment_rank_count,
                holy_suit_effect_count, material_policy_count,
                material_recipe_count, holy_suit_tier_count,
                holy_suit_upgrade_count, holy_suit_consumable_count,
                holy_suit_policy_count)
            VALUES (
                @revision, @entryCount, @source, 9,
                @attributeCount, @equipmentRankCount,
                @holySuitEffectCount, @materialPolicyCount,
                @materialRecipeCount, @holySuitTierCount,
                @holySuitUpgradeCount, @holySuitConsumableCount, 1)
            ON CONFLICT (revision) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("entryCount", entryCount);
        command.Parameters.AddWithValue("source", PublicationSource);
        command.Parameters.AddWithValue(
            "attributeCount",
            policies.Attributes.Count);
        command.Parameters.AddWithValue(
            "equipmentRankCount",
            policies.EquipmentRanks.Count);
        command.Parameters.AddWithValue(
            "holySuitEffectCount",
            policies.HolySuitEffects.Count);
        command.Parameters.AddWithValue(
            "materialPolicyCount",
            policies.MaterialPolicyCount);
        command.Parameters.AddWithValue(
            "materialRecipeCount",
            policies.MaterialRecipeCount);
        command.Parameters.AddWithValue(
            "holySuitTierCount",
            holySuit.Tiers.Count);
        command.Parameters.AddWithValue(
            "holySuitUpgradeCount",
            holySuit.Upgrades.Count);
        command.Parameters.AddWithValue(
            "holySuitConsumableCount",
            holySuit.Consumables.Count);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task VerifyDefinitionIntegrityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        IReadOnlyList<ItemTemplateDefinition> expectedDefinitions,
        ItemPolicySnapshot expectedPolicies,
        HolySuitPolicySnapshot expectedHolySuit,
        CancellationToken cancellationToken)
    {
        var definitions = await ReadPublishedDefinitionsAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var policies = await ReadPublishedPoliciesAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var holySuit = await ReadPublishedHolySuitPoliciesAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        if (definitions.Count != expectedDefinitions.Count ||
            policies.Attributes.Count != expectedPolicies.Attributes.Count ||
            policies.EquipmentRanks.Count !=
                expectedPolicies.EquipmentRanks.Count ||
            policies.HolySuitEffects.Count !=
                expectedPolicies.HolySuitEffects.Count ||
            policies.MaterialPolicyCount !=
                expectedPolicies.MaterialPolicyCount ||
            policies.MaterialRecipeCount !=
                expectedPolicies.MaterialRecipeCount ||
            holySuit.Tiers.Count != expectedHolySuit.Tiers.Count ||
            holySuit.Upgrades.Count != expectedHolySuit.Upgrades.Count ||
            holySuit.Consumables.Count != expectedHolySuit.Consumables.Count ||
            !ItemTemplateContentRevisionHasher.ComputeV6(
                    definitions,
                    policies.Attributes,
                    policies.EquipmentRanks,
                    policies.HolySuitEffects,
                    policies.ForgingMaterials,
                    policies.EnhancementMaterials,
                    policies.AttributeDusts,
                    policies.Recipes,
                    holySuit.Tiers,
                    holySuit.Upgrades,
                    holySuit.Consumables,
                    holySuit.OperationPolicy)
                .Equals(revision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Item-content revision {revision} failed count or " +
                "hash validation before publication.");
        }
    }

    private static async Task PublishRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_publication (
                family, revision, published_at)
            VALUES ('items', @revision, now())
            ON CONFLICT (family) DO UPDATE
            SET revision = EXCLUDED.revision,
                published_at = EXCLUDED.published_at;
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
