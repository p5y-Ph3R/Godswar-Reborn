using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private sealed record V7PublicationSnapshot(
        IReadOnlyList<ItemTemplateDefinition> Definitions,
        ItemPolicySnapshot Policies,
        HolySuitPolicySnapshot HolySuit);

    private static async Task<V7PublicationSnapshot> PrepareV7PublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState? existing,
        CancellationToken cancellationToken)
    {
        V6PublicationSnapshot prior;
        if (existing is { ManifestVersion: 7 })
        {
            await VerifyPublishedV7ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            prior = new V6PublicationSnapshot(
                await ReadPublishedDefinitionsAsync(
                    connection, transaction, existing.Revision, cancellationToken),
                await ReadPublishedPoliciesAsync(
                    connection, transaction, existing.Revision, cancellationToken),
                await ReadPublishedHolySuitPoliciesAsync(
                    connection, transaction, existing.Revision, cancellationToken));
        }
        else if (existing is { ManifestVersion: 6 })
        {
            await VerifyPublishedV6ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            prior = new V6PublicationSnapshot(
                await ReadPublishedDefinitionsAsync(
                    connection, transaction, existing.Revision, cancellationToken),
                await ReadPublishedPoliciesAsync(
                    connection, transaction, existing.Revision, cancellationToken),
                await ReadPublishedHolySuitPoliciesAsync(
                    connection, transaction, existing.Revision, cancellationToken));
        }
        else
        {
            prior = await PrepareV6PublicationAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
        }

        var definitions = await AppendMissingReviewedMaterialsAsync(
            connection,
            transaction,
            prior.Definitions,
            cancellationToken);
        var attributes = await AppendElementalAttributesAsync(
            connection,
            transaction,
            prior.Policies.Attributes,
            cancellationToken);
        var policies = prior.Policies with
        {
            Attributes = attributes,
            EnhancementMaterials = ReviewedEnhancementMaterials
        };
        return new V7PublicationSnapshot(
            definitions,
            policies,
            prior.HolySuit);
    }

    private static async Task<IReadOnlyList<ItemAttributeDefinition>>
        AppendElementalAttributesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemAttributeDefinition> prior,
            CancellationToken cancellationToken)
    {
        var seeds = ElementalItemContentBaseline.Attributes;
        await using var command = new NpgsqlCommand("""
            SELECT input.id,
                   input.level_values::numeric[]::text,
                   input.stats::jsonb::text
            FROM unnest(
                @ids,
                @levelValues,
                @statsJson
            ) AS input(id, level_values, stats)
            ORDER BY input.id;
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter(
            "ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = seeds.Select(static value => value.Id).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter(
            "levelValues", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = seeds.Select(static value => value.LevelValues).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter(
            "statsJson", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = seeds.Select(static value => value.StatsJson).ToArray()
        });

        var canonical = new Dictionary<int, (string Levels, string Stats)>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            canonical.Add(
                reader.GetInt32(0),
                (reader.GetString(1), reader.GetString(2)));
        }
        if (canonical.Count != seeds.Count)
        {
            throw new InvalidDataException(
                "Elemental attribute canonicalization was incomplete.");
        }

        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var seed in seeds)
        {
            var canonicalValue = seed with
            {
                LevelValues = canonical[seed.Id].Levels,
                StatsJson = canonical[seed.Id].Stats
            };
            if (!byId.TryAdd(seed.Id, canonicalValue) &&
                !ElementalAttributeDefinitionsEqual(
                    byId[seed.Id],
                    canonicalValue))
            {
                throw new InvalidOperationException(
                    $"Elemental attribute {seed.Id} conflicts with published content.");
            }
        }
        return byId.Values.OrderBy(static value => value.Id).ToArray();
    }

    private static async Task<bool> PublishedElementalContentIsCompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var reviewedItems =
            await ReadCanonicalReviewedElementalStoneItemsAsync(
                connection,
                transaction,
                cancellationToken);
        var publishedItems = await ReadElementalStoneTemplatesAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            revision,
            cancellationToken);
        ValidateElementalStoneTemplates(
            publishedItems,
            reviewedItems,
            $"published revision {revision}");

        var policies = await ReadPublishedPoliciesAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var reviewedAttributes = await AppendElementalAttributesAsync(
            connection,
            transaction,
            [],
            cancellationToken);
        var publishedAttributes = policies.Attributes
            .Where(static value => value.Id is >= 480 and <= 500)
            .OrderBy(static value => value.Id)
            .ToArray();
        if (publishedAttributes.Length != reviewedAttributes.Count ||
            !publishedAttributes.Zip(reviewedAttributes)
                .All(static pair => ElementalAttributeDefinitionsEqual(
                    pair.First,
                    pair.Second)))
        {
            throw new InvalidOperationException(
                "Published elemental attribute policies conflict with the reviewed definitions.");
        }

        var reviewedMaterials = GearEnhancementMaterialCatalog.All
            .Where(static value =>
                value.ItemId is >= 16300 and <= 16320)
            .OrderBy(static value => value.ItemId)
            .ToArray();
        var publishedMaterials = policies.EnhancementMaterials
            .Where(static value =>
                value.ItemId is >= 16300 and <= 16320)
            .OrderBy(static value => value.ItemId)
            .ToArray();
        return publishedMaterials.Length == reviewedMaterials.Length &&
            publishedMaterials.Zip(reviewedMaterials)
                .All(static pair => ElementalMaterialDefinitionsEqual(
                    pair.First,
                    pair.Second));
    }

    private static bool ElementalAttributeDefinitionsEqual(
        ItemAttributeDefinition left,
        ItemAttributeDefinition right) =>
        left.Id == right.Id &&
        left.NameKey.Equals(right.NameKey, StringComparison.Ordinal) &&
        left.StatType == right.StatType &&
        left.Distribution.SequenceEqual(right.Distribution) &&
        left.Percent == right.Percent &&
        left.MaxLevel == right.MaxLevel &&
        left.LevelValues.Equals(right.LevelValues, StringComparison.Ordinal) &&
        left.StatsJson.Equals(right.StatsJson, StringComparison.Ordinal);

    private static bool ElementalMaterialDefinitionsEqual(
        GearEnhancementMaterialDefinition left,
        GearEnhancementMaterialDefinition right) =>
        left.ItemId == right.ItemId &&
        left.NameKey.Equals(right.NameKey, StringComparison.Ordinal) &&
        left.DisplayName.Equals(right.DisplayName, StringComparison.Ordinal) &&
        left.Kind == right.Kind &&
        left.Texture.Equals(right.Texture, StringComparison.Ordinal) &&
        left.Icon.Equals(right.Icon, StringComparison.Ordinal) &&
        left.StackCap == right.StackCap &&
        left.Random == right.Random &&
        left.Distribution.Equals(right.Distribution, StringComparison.Ordinal) &&
        left.AttributeName == right.AttributeName &&
        left.AllowedAttributeIds.SequenceEqual(right.AllowedAttributeIds) &&
        left.CanEnhance == right.CanEnhance &&
        left.SourceAttributeLevel == right.SourceAttributeLevel &&
        left.TargetAttributeLevel == right.TargetAttributeLevel;

    private static async Task VerifyPublishedV7ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState release,
        CancellationToken cancellationToken)
    {
        if (release.ManifestVersion != 7)
        {
            throw new InvalidOperationException(
                $"Expected item manifest v7, found {release.ManifestVersion}.");
        }

        var definitions = await ReadPublishedDefinitionsAsync(
            connection, transaction, release.Revision, cancellationToken);
        var policies = await ReadPublishedPoliciesAsync(
            connection, transaction, release.Revision, cancellationToken);
        var holySuit = await ReadPublishedHolySuitPoliciesAsync(
            connection, transaction, release.Revision, cancellationToken);
        if (definitions.Count != release.EntryCount ||
            policies.Attributes.Count != release.AttributeCount ||
            policies.MaterialPolicyCount != release.MaterialPolicyCount ||
            policies.MaterialRecipeCount != release.MaterialRecipeCount ||
            holySuit.Tiers.Count != release.HolySuitTierCount ||
            holySuit.Upgrades.Count != release.HolySuitUpgradeCount ||
            holySuit.Consumables.Count != release.HolySuitConsumableCount ||
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
                .Equals(release.Revision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Published item-content revision {release.Revision} failed manifest-v7 validation.");
        }
    }
}
