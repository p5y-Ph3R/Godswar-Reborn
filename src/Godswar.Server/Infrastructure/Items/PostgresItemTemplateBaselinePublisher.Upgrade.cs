using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private sealed record V4PublicationSnapshot(
        IReadOnlyList<ItemTemplateDefinition> Definitions,
        ItemPolicySnapshot Policies);

    private static async Task<V4PublicationSnapshot> PrepareV4PublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState? existing,
        CancellationToken cancellationToken)
    {
        if (existing is null)
        {
            await UpsertReviewedBaselineAsync(connection, transaction, cancellationToken);
            await ApplyCompatibilityOverridesAsync(connection, transaction, cancellationToken);
            return new V4PublicationSnapshot(
                await ReadAuthoritativeDefinitionsAsync(
                    connection, transaction, cancellationToken),
                await ReadAuthoritativePoliciesAsync(
                    connection, transaction, cancellationToken));
        }

        if (existing.ManifestVersion is not (1 or 2 or 3) ||
            (existing.ManifestVersion is 1 or 2 &&
             (existing.MaterialPolicyCount != 0 ||
              existing.MaterialRecipeCount != 0)) ||
            existing.ManifestVersion == 3 &&
            (existing.MaterialPolicyCount <= 0 ||
             existing.MaterialRecipeCount != 0))
        {
            throw new InvalidOperationException(
                $"Item-content manifest version {existing.ManifestVersion} " +
                "cannot be upgraded to version 4.");
        }

        var prior = await ReadPublishedDefinitionsAsync(
            connection, transaction, existing.Revision, cancellationToken);
        ItemPolicySnapshot policies;
        if (existing.ManifestVersion == 1)
        {
            if (prior.Count != existing.EntryCount ||
                !ItemTemplateContentRevisionHasher.ComputeLegacyV1(prior)
                    .Equals(existing.Revision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Legacy item-content revision {existing.Revision} " +
                    "failed canonical count or hash validation.");
            }
            policies = await ReadAuthoritativePoliciesAsync(
                connection, transaction, cancellationToken);
        }
        else if (existing.ManifestVersion == 2)
        {
            policies = await ReadPublishedPoliciesAsync(
                connection, transaction, existing.Revision, cancellationToken);
            if (prior.Count != existing.EntryCount ||
                policies.Attributes.Count != existing.AttributeCount ||
                policies.EquipmentRanks.Count != existing.EquipmentRankCount ||
                policies.HolySuitEffects.Count != existing.HolySuitEffectCount ||
                policies.MaterialPolicyCount != 0 ||
                !ItemTemplateContentRevisionHasher.ComputeLegacyV2(
                        prior,
                        policies.Attributes,
                        policies.EquipmentRanks,
                        policies.HolySuitEffects)
                    .Equals(existing.Revision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Version-2 item-content revision {existing.Revision} " +
                    "failed canonical count or hash validation.");
            }
        }
        else
        {
            policies = await ReadPublishedPoliciesAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            if (prior.Count != existing.EntryCount ||
                policies.Attributes.Count != existing.AttributeCount ||
                policies.EquipmentRanks.Count != existing.EquipmentRankCount ||
                policies.HolySuitEffects.Count != existing.HolySuitEffectCount ||
                policies.MaterialPolicyCount != existing.MaterialPolicyCount ||
                policies.MaterialRecipeCount != 0 ||
                !ItemTemplateContentRevisionHasher.Compute(
                        prior,
                        policies.Attributes,
                        policies.EquipmentRanks,
                        policies.HolySuitEffects,
                        policies.ForgingMaterials,
                        policies.EnhancementMaterials,
                        policies.AttributeDusts)
                    .Equals(existing.Revision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Version-3 item-content revision {existing.Revision} " +
                    "failed canonical count or hash validation.");
            }
        }

        var definitions = await AppendMissingReviewedSkillBooksAsync(
            connection, transaction, prior, cancellationToken);
        definitions = await AppendMissingReviewedMaterialsAsync(
            connection, transaction, definitions, cancellationToken);
        policies = existing.ManifestVersion == 3
            ? policies with
            {
                // A hash-valid v3 publication already owns its material
                // policies. Preserve those immutable database values and
                // append only the recipe family introduced by v4.
                Recipes = ReviewedMaterialRecipes
            }
            : policies with
            {
                ForgingMaterials = ReviewedForgingMaterials,
                EnhancementMaterials = ReviewedEnhancementMaterials,
                AttributeDusts = ReviewedAttributeDusts,
                Recipes = ReviewedMaterialRecipes
            };
        return new V4PublicationSnapshot(definitions, policies);
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        AppendMissingReviewedMaterialsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var seeds = ForgingMaterialCatalog.All.Select(
                static value => value.ToItemTemplateSeed())
            .Concat(GearEnhancementMaterialCatalog.All.Select(
                static value => value.ToItemTemplateSeed()))
            .Concat(GearMentorMaterialCatalog.AttributeDusts.Select(
                static value => value.ToItemTemplateSeed()))
            .OrderBy(static value => value.Id)
            .ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT input.item_id, input.stats::jsonb::text
            FROM unnest(@itemIds, @statsJson) AS input(item_id, stats)
            ORDER BY input.item_id;
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = seeds.Select(static value => value.Id).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter(
            "statsJson", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = seeds.Select(static value => value.StatsJson).ToArray()
        });
        var canonical = new Dictionary<int, string>(seeds.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            canonical.Add(reader.GetInt32(0), reader.GetString(1));
        if (canonical.Count != seeds.Length)
            throw new InvalidDataException(
                "Reviewed item-material JSON canonicalization was incomplete.");

        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var seed in seeds)
        {
            var reviewed = ToDefinition(seed) with { StatsJson = canonical[seed.Id] };
            if (byId.TryGetValue(reviewed.Id, out var existing))
            {
                if (!DefinitionsEquivalent(existing, reviewed))
                    throw new InvalidOperationException(
                        $"Reviewed material {reviewed.Id} conflicts with " +
                        "the published item definition.");
                continue;
            }
            byId.Add(reviewed.Id, reviewed);
        }
        return byId.Values.OrderBy(static value => value.Id).ToArray();
    }
}
