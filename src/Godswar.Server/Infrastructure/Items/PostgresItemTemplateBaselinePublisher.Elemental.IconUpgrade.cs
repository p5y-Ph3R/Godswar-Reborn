using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private sealed record V8PublicationSnapshot(
        IReadOnlyList<ItemTemplateDefinition> Definitions,
        ItemPolicySnapshot Policies,
        HolySuitPolicySnapshot HolySuit);

    private static async Task<V8PublicationSnapshot> PrepareV8PublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState? existing,
        CancellationToken cancellationToken)
    {
        V7PublicationSnapshot prior;
        if (existing is { ManifestVersion: 8 })
        {
            await VerifyPublishedV8ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            prior = await ReadV7ShapeAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        }
        else if (existing is { ManifestVersion: 7 })
        {
            await VerifyPublishedV7ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            prior = await ReadV7ShapeAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        }
        else
        {
            prior = await PrepareV7PublicationAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
        }

        return new V8PublicationSnapshot(
            await ReplaceReviewedElementalStoneItemsAsync(
                connection,
                transaction,
                prior.Definitions,
                cancellationToken),
            prior.Policies with
            {
                EnhancementMaterials = ReviewedEnhancementMaterials
            },
            prior.HolySuit);
    }

    private static async Task<V7PublicationSnapshot> ReadV7ShapeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken) =>
        new(
            await ReadPublishedDefinitionsAsync(
                connection, transaction, revision, cancellationToken),
            await ReadPublishedPoliciesAsync(
                connection, transaction, revision, cancellationToken),
            await ReadPublishedHolySuitPoliciesAsync(
                connection, transaction, revision, cancellationToken));

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReplaceReviewedElementalStoneItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedElementalStoneItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            if (!byId.ContainsKey(definition.Id))
            {
                throw new InvalidOperationException(
                    $"Elemental stone {definition.Id} is missing from " +
                    "the prior immutable item release.");
            }

            byId[definition.Id] = definition;
        }

        return byId.Values.OrderBy(static value => value.Id).ToArray();
    }

    private static async Task VerifyPublishedV8ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState release,
        CancellationToken cancellationToken)
    {
        if (release.ManifestVersion != 8)
        {
            throw new InvalidOperationException(
                $"Expected item manifest v8, found {release.ManifestVersion}.");
        }

        var definitions = await ReadPublishedDefinitionsAsync(
            connection, transaction, release.Revision, cancellationToken);
        var policies = await ReadPublishedPoliciesAsync(
            connection, transaction, release.Revision, cancellationToken);
        var holySuit = await ReadPublishedHolySuitPoliciesAsync(
            connection, transaction, release.Revision, cancellationToken);
        if (definitions.Count != release.EntryCount ||
            policies.Attributes.Count != release.AttributeCount ||
            policies.EquipmentRanks.Count != release.EquipmentRankCount ||
            policies.HolySuitEffects.Count != release.HolySuitEffectCount ||
            policies.MaterialPolicyCount != release.MaterialPolicyCount ||
            policies.MaterialRecipeCount != release.MaterialRecipeCount ||
            holySuit.Tiers.Count != release.HolySuitTierCount ||
            holySuit.Upgrades.Count != release.HolySuitUpgradeCount ||
            holySuit.Consumables.Count != release.HolySuitConsumableCount ||
            release.HolySuitPolicyCount != 1 ||
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
                $"Published item-content revision {release.Revision} " +
                "failed manifest-v8 validation.");
        }
    }
}
