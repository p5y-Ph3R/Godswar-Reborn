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
        "reviewed-item-content-v4+skillbooks-v1+materials-v1+recipes-v1";

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
        if (existing is { ManifestVersion: 4 })
        {
            await VerifyPublishedReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ItemTemplatePublicationResult(
                existing.Revision,
                existing.EntryCount,
                Created: false);
        }

        var snapshot = await PrepareV4PublicationAsync(
            connection,
            transaction,
            existing,
            cancellationToken);
        var definitions = snapshot.Definitions;
        var policies = snapshot.Policies;
        var revision = ItemTemplateContentRevisionHasher.Compute(
            definitions,
            policies.Attributes,
            policies.EquipmentRanks,
            policies.HolySuitEffects,
            policies.ForgingMaterials,
            policies.EnhancementMaterials,
            policies.AttributeDusts,
            policies.Recipes);

        var created = await InsertRevisionAsync(
            connection,
            transaction,
            revision,
            definitions.Count,
            policies,
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
                cancellationToken);
        }
        await VerifyDefinitionIntegrityAsync(
            connection,
            transaction,
            revision,
            definitions,
            policies,
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
                if (!reader.GetBoolean(9))
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
                    reader.GetInt32(8));
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
                static material => material.ToItemTemplateSeed()));
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

    private static async Task<bool> InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        int entryCount,
        ItemPolicySnapshot policies,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_revisions (
                revision, entry_count, source, manifest_version,
                attribute_count, equipment_rank_count,
                holy_suit_effect_count, material_policy_count,
                material_recipe_count)
            VALUES (
                @revision, @entryCount, @source, 4,
                @attributeCount, @equipmentRankCount,
                @holySuitEffectCount, @materialPolicyCount,
                @materialRecipeCount)
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
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task VerifyDefinitionIntegrityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        IReadOnlyList<ItemTemplateDefinition> expectedDefinitions,
        ItemPolicySnapshot expectedPolicies,
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
            !ItemTemplateContentRevisionHasher.Compute(
                    definitions,
                    policies.Attributes,
                    policies.EquipmentRanks,
                    policies.HolySuitEffects,
                    policies.ForgingMaterials,
                    policies.EnhancementMaterials,
                    policies.AttributeDusts,
                    policies.Recipes)
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
