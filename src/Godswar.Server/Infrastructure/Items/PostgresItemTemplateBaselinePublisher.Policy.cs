using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;
using Godswar.Server.State;
using Godswar.Server.Infrastructure.Database;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private sealed record PublishedItemRevisionState(
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
        int HolySuitPolicyCount);

    private sealed record ItemPolicySnapshot(
        IReadOnlyList<ItemAttributeDefinition> Attributes,
        IReadOnlyList<EquipmentRankDefinition> EquipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> HolySuitEffects,
        IReadOnlyList<ForgingMaterialDefinition> ForgingMaterials,
        IReadOnlyList<GearEnhancementMaterialDefinition> EnhancementMaterials,
        IReadOnlyList<AttributeDustDefinition> AttributeDusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> Recipes)
    {
        public int MaterialPolicyCount =>
            ForgingMaterials.Count + EnhancementMaterials.Count +
            AttributeDusts.Count;

        public int MaterialRecipeCount => Recipes.Count;
    }

    private static IReadOnlyList<ItemTemplateDefinition>
        AppendMissingReviewedSkillBooks(
            IReadOnlyList<ItemTemplateDefinition> prior,
            IReadOnlyDictionary<int, string>? canonicalStats = null)
    {
        var byId = prior.ToDictionary(static definition => definition.Id);
        foreach (var skillBook in SkillTalentSeeds.SkillBooks
                     .OrderBy(static value => value.ItemId))
        {
            var seed = skillBook.ToItemTemplateSeed();
            var statsJson = canonicalStats is not null &&
                            canonicalStats.TryGetValue(
                                skillBook.ItemId,
                                out var canonical)
                ? canonical
                : seed.StatsJson;
            var reviewed = new ItemTemplateDefinition(
                checked((uint)seed.Id),
                seed.Kind,
                seed.NameKey,
                seed.DisplayName,
                seed.EquipmentSlot,
                Array.AsReadOnly(seed.ClassIds.ToArray()),
                seed.MinLevel,
                seed.MaxLevel,
                seed.Hand,
                seed.SkillFlag,
                seed.Texture,
                seed.Icon,
                statsJson);
            if (byId.TryGetValue(reviewed.Id, out var existing))
            {
                if (!DefinitionsEquivalent(existing, reviewed))
                {
                    throw new InvalidOperationException(
                        $"Reviewed skill book {reviewed.Id} conflicts " +
                        "with the published item definition.");
                }
                continue;
            }

            byId.Add(reviewed.Id, reviewed);
        }

        return byId.Values.OrderBy(static definition => definition.Id)
            .ToArray();
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        AppendMissingReviewedSkillBooksAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var books = SkillTalentSeeds.SkillBooks
            .OrderBy(static value => value.ItemId)
            .ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT input.item_id, input.stats::jsonb::text
            FROM unnest(@itemIds, @statsJson) AS input(item_id, stats)
            ORDER BY input.item_id;
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = books.Select(static value => value.ItemId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter(
            "statsJson",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = books.Select(static value => value.StatsJson).ToArray()
        });
        var canonicalStats = new Dictionary<int, string>(books.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            canonicalStats.Add(reader.GetInt32(0), reader.GetString(1));
        }

        if (canonicalStats.Count != books.Length)
        {
            throw new InvalidDataException(
                "Reviewed skill-book JSON canonicalization was incomplete.");
        }

        return AppendMissingReviewedSkillBooks(prior, canonicalStats);
    }

    private static void ValidateReviewedSkillBookConflicts()
    {
        var baseline = ItemTemplateSeeds.All
            .Select(ToDefinition)
            .ToArray();
        _ = AppendMissingReviewedSkillBooks(baseline);
    }

    private static ItemTemplateDefinition ToDefinition(
        ItemTemplateSeed seed) =>
        new(
            checked((uint)seed.Id),
            seed.Kind,
            seed.NameKey,
            seed.DisplayName,
            seed.EquipmentSlot,
            Array.AsReadOnly(seed.ClassIds.ToArray()),
            seed.MinLevel,
            seed.MaxLevel,
            seed.Hand,
            seed.SkillFlag,
            seed.Texture,
            seed.Icon,
            seed.StatsJson);

    private static bool DefinitionsEquivalent(
        ItemTemplateDefinition left,
        ItemTemplateDefinition right) =>
        left.Id == right.Id &&
        left.Kind.Equals(right.Kind, StringComparison.Ordinal) &&
        left.NameKey.Equals(right.NameKey, StringComparison.Ordinal) &&
        left.DisplayName.Equals(right.DisplayName, StringComparison.Ordinal) &&
        left.EquipmentSlot == right.EquipmentSlot &&
        left.ClassIds.SequenceEqual(right.ClassIds) &&
        left.MinLevel == right.MinLevel &&
        left.MaxLevel == right.MaxLevel &&
        left.Hand == right.Hand &&
        left.SkillFlag == right.SkillFlag &&
        left.Texture.Equals(right.Texture, StringComparison.Ordinal) &&
        left.Icon.Equals(right.Icon, StringComparison.Ordinal) &&
        left.StatsJson.Equals(right.StatsJson, StringComparison.Ordinal);

    private static async Task VerifyPublishedReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState release,
        CancellationToken cancellationToken)
    {
        var definitions = await ReadPublishedDefinitionsAsync(
            connection,
            transaction,
            release.Revision,
            cancellationToken);
        var policies = await ReadPublishedPoliciesAsync(
            connection,
            transaction,
            release.Revision,
            cancellationToken);
        var holySuit = await ReadPublishedHolySuitPoliciesAsync(
            connection,
            transaction,
            release.Revision,
            cancellationToken);
        if (release.ManifestVersion != 5 ||
            release.MaterialPolicyCount <= 0 ||
            release.MaterialRecipeCount <= 0 ||
            definitions.Count != release.EntryCount ||
            policies.Attributes.Count != release.AttributeCount ||
            policies.EquipmentRanks.Count != release.EquipmentRankCount ||
            policies.HolySuitEffects.Count != release.HolySuitEffectCount ||
            policies.MaterialPolicyCount != release.MaterialPolicyCount ||
            policies.MaterialRecipeCount != release.MaterialRecipeCount ||
            holySuit.Tiers.Count != release.HolySuitTierCount ||
            holySuit.Upgrades.Count != release.HolySuitUpgradeCount ||
            holySuit.Consumables.Count != release.HolySuitConsumableCount ||
            release.HolySuitPolicyCount != 1 ||
            policies.Attributes.Count == 0 ||
            policies.EquipmentRanks.Count == 0 ||
            policies.HolySuitEffects.Count == 0 ||
            !ItemTemplateContentRevisionHasher.Compute(
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
                .Equals(release.Revision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Published item-content revision {release.Revision} " +
                "failed manifest count or hash validation.");
        }
    }

    private static async Task<ItemPolicySnapshot>
        ReadAuthoritativePoliciesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using (var seedAttributes = new NpgsqlCommand(
                         PostgresRelationalContentBaselineBootstrapper
                             .LoadReviewedItemAttributeSeedSql(),
                         connection,
                         transaction))
        {
            seedAttributes.CommandTimeout = 120;
            await seedAttributes.ExecuteNonQueryAsync(cancellationToken);
        }

        return new(
            await ReadAttributesAsync(
                connection,
                transaction,
                revision: null,
                cancellationToken),
            await ReadEquipmentRanksAsync(
                connection,
                transaction,
                revision: null,
                cancellationToken),
            await ReadHolySuitEffectsAsync(
                connection,
                transaction,
                revision: null,
                cancellationToken),
            ReviewedForgingMaterials,
            ReviewedEnhancementMaterials,
            ReviewedAttributeDusts,
            ReviewedMaterialRecipes);
    }

    private static async Task<ItemPolicySnapshot>
        ReadPublishedPoliciesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var attributes = await ReadAttributesAsync(
                connection,
                transaction,
                revision,
                cancellationToken);
        var ranks = await ReadEquipmentRanksAsync(
                connection,
                transaction,
                revision,
                cancellationToken);
        var holy = await ReadHolySuitEffectsAsync(
                connection,
                transaction,
                revision,
                cancellationToken);
        var materials = await ReadPublishedMaterialPoliciesAsync(
            connection, transaction, revision, cancellationToken);
        return new ItemPolicySnapshot(
            attributes,
            ranks,
            holy,
            materials.Forging,
            materials.Enhancement,
            materials.Dusts,
            materials.Recipes);
    }

    private static async Task<IReadOnlyList<ItemAttributeDefinition>>
        ReadAttributesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string? revision,
            CancellationToken cancellationToken)
    {
        var values = new List<ItemAttributeDefinition>();
        var sql = revision is null
            ? """
              SELECT id, name_key, stat_type, distribution, percent,
                     max_level, level_values::text, stats::text
              FROM item_attribute_templates
              ORDER BY id;
              """
            : """
              SELECT id, name_key, stat_type, distribution, percent,
                     max_level, level_values::text, stats::text
              FROM item_attribute_content_definitions
              WHERE revision = @revision
              ORDER BY id;
              """;
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        if (revision is not null)
        {
            command.Parameters.AddWithValue("revision", revision);
        }
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new ItemAttributeDefinition(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt16(2),
                reader.GetFieldValue<short[]>(3),
                reader.GetBoolean(4),
                reader.GetInt16(5),
                reader.GetString(6),
                reader.GetString(7)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<EquipmentRankDefinition>>
        ReadEquipmentRanksAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string? revision,
            CancellationToken cancellationToken)
    {
        var values = new List<EquipmentRankDefinition>();
        var sql = revision is null
            ? """
              SELECT rank_kind, rank_level, required_score,
                     aura_effect, source
              FROM equipment_rank_rules
              ORDER BY rank_kind, rank_level;
              """
            : """
              SELECT rank_kind, rank_level, required_score,
                     aura_effect, source
              FROM equipment_rank_content_definitions
              WHERE revision = @revision
              ORDER BY rank_kind, rank_level;
              """;
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        if (revision is not null)
        {
            command.Parameters.AddWithValue("revision", revision);
        }
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new EquipmentRankDefinition(
                reader.GetString(0),
                reader.GetInt16(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<HolySuitEffectDefinition>>
        ReadHolySuitEffectsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string? revision,
            CancellationToken cancellationToken)
    {
        var values = new List<HolySuitEffectDefinition>();
        var sql = revision is null
            ? """
              SELECT effect_key, stat_type, unlock_points,
                     effect_value::text, source
              FROM holy_suit_effect_templates
              ORDER BY effect_key;
              """
            : """
              SELECT effect_key, stat_type, unlock_points,
                     effect_value::text, source
              FROM holy_suit_effect_content_definitions
              WHERE revision = @revision
              ORDER BY effect_key;
              """;
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        if (revision is not null)
        {
            command.Parameters.AddWithValue("revision", revision);
        }
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new HolySuitEffectDefinition(
                reader.GetString(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return values;
    }

    private static async Task InsertDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        IReadOnlyList<ItemTemplateDefinition> definitions,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_definitions (
                revision, id, kind, name_key, display_name,
                equipment_slot, class_ids, min_level, max_level, hand,
                skill_flag, texture, icon, stats)
            VALUES (
                @revision, @id, @kind, @nameKey, @displayName,
                @equipmentSlot, @classIds, @minLevel, @maxLevel, @hand,
                @skillFlag, @texture, @icon, @stats)
            ON CONFLICT (revision, id) DO NOTHING;
            """, connection, transaction);
        foreach (var definition in definitions)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("id", checked((int)definition.Id));
            command.Parameters.AddWithValue("kind", definition.Kind);
            command.Parameters.AddWithValue("nameKey", definition.NameKey);
            command.Parameters.AddWithValue(
                "displayName",
                definition.DisplayName);
            command.Parameters.AddWithValue(
                "equipmentSlot",
                definition.EquipmentSlot);
            command.Parameters.Add(new NpgsqlParameter(
                "classIds",
                NpgsqlDbType.Array | NpgsqlDbType.Smallint)
            {
                Value = definition.ClassIds.ToArray()
            });
            AddNullable(command, "minLevel", definition.MinLevel);
            AddNullable(command, "maxLevel", definition.MaxLevel);
            AddNullable(command, "hand", definition.Hand);
            AddNullable(command, "skillFlag", definition.SkillFlag);
            command.Parameters.AddWithValue("texture", definition.Texture);
            command.Parameters.AddWithValue("icon", definition.Icon);
            command.Parameters.Add(new NpgsqlParameter("stats", NpgsqlDbType.Jsonb)
            {
                Value = definition.StatsJson
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddNullable<T>(
        NpgsqlCommand command,
        string name,
        T? value)
        where T : struct
    {
        var databaseType = typeof(T) == typeof(short)
            ? NpgsqlDbType.Smallint
            : NpgsqlDbType.Integer;
        command.Parameters.Add(new NpgsqlParameter(name, databaseType)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });
    }

    private static async Task InsertPoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        ItemPolicySnapshot policies,
        HolySuitPolicySnapshot holySuit,
        CancellationToken cancellationToken)
    {
        await InsertCorePoliciesAsync(
            connection, transaction, revision, policies, cancellationToken);
        await InsertMaterialPoliciesAsync(
            connection,
            transaction,
            revision,
            policies,
            cancellationToken);
        await InsertHolySuitPoliciesAsync(
            connection,
            transaction,
            revision,
            holySuit,
            cancellationToken);
    }
}
