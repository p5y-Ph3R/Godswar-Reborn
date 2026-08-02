using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private sealed record V6PublicationSnapshot(
        IReadOnlyList<ItemTemplateDefinition> Definitions,
        ItemPolicySnapshot Policies,
        HolySuitPolicySnapshot HolySuit);

    private static async Task<V6PublicationSnapshot> PrepareV6PublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState? existing,
        CancellationToken cancellationToken)
    {
        V5PublicationSnapshot prior;
        if (existing is { ManifestVersion: 6 })
        {
            await VerifyPublishedV6ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            var holySuit = await ReadPublishedHolySuitPoliciesAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            prior = new V5PublicationSnapshot(
                await ReadPublishedDefinitionsAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken),
                await ReadPublishedPoliciesAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken),
                new HolySuitPolicySnapshot(
                    holySuit.Tiers,
                    holySuit.Upgrades,
                    holySuit.Consumables,
                    ReviewedHolySuitPolicy.OperationPolicy));
        }
        else if (existing is { ManifestVersion: 5 })
        {
            await VerifyPublishedReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            var holySuit = await ReadPublishedHolySuitPoliciesAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            prior = new V5PublicationSnapshot(
                await ReadPublishedDefinitionsAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken),
                await ReadPublishedPoliciesAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken),
                new HolySuitPolicySnapshot(
                    holySuit.Tiers,
                    holySuit.Upgrades,
                    holySuit.Consumables,
                    holySuit.OperationPolicy));
        }
        else
        {
            prior = await PrepareV5PublicationAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
        }

        return new V6PublicationSnapshot(
            await AppendMissingReviewedClassSuitItemsAsync(
                connection,
                transaction,
                prior.Definitions,
                cancellationToken),
            prior.Policies,
            prior.HolySuit);
    }

    private static async Task VerifyPublishedV6ReleaseAsync(
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
        if (release.ManifestVersion != 6 ||
            release.MaterialPolicyCount <= 0 ||
            release.MaterialRecipeCount <= 0 ||
            definitions.Count != release.EntryCount ||
            policies.Attributes.Count != release.AttributeCount ||
            policies.EquipmentRanks.Count != release.EquipmentRankCount ||
            policies.HolySuitEffects.Count !=
                release.HolySuitEffectCount ||
            policies.MaterialPolicyCount != release.MaterialPolicyCount ||
            policies.MaterialRecipeCount != release.MaterialRecipeCount ||
            holySuit.Tiers.Count != release.HolySuitTierCount ||
            holySuit.Upgrades.Count != release.HolySuitUpgradeCount ||
            holySuit.Consumables.Count !=
                release.HolySuitConsumableCount ||
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
                "failed manifest-v6 count or hash validation.");
        }
    }
}
