using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private sealed record V9PublicationSnapshot(
        IReadOnlyList<ItemTemplateDefinition> Definitions,
        ItemPolicySnapshot Policies,
        HolySuitPolicySnapshot HolySuit);

    private static async Task<V9PublicationSnapshot> PrepareV9PublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState? existing,
        CancellationToken cancellationToken)
    {
        V8PublicationSnapshot prior;
        if (existing is { ManifestVersion: 9 })
        {
            await VerifyPublishedV9ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            prior = await ReadV8ShapeAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        }
        else if (existing is { ManifestVersion: 8 })
        {
            await VerifyPublishedV8ReleaseAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            await ValidateOfficialV8ElementalReleaseAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
            prior = await ReadV8ShapeAsync(
                connection,
                transaction,
                existing.Revision,
                cancellationToken);
        }
        else
        {
            prior = await PrepareV8PublicationAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
        }

        var definitions = await ReplaceReviewedElementalStoneItemsAsync(
            connection,
            transaction,
            prior.Definitions,
            cancellationToken);
        definitions = await AppendMissingReviewedSocketSpellsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReconcileReviewedHolyStoneMaterialsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        return new V9PublicationSnapshot(
            definitions,
            prior.Policies with
            {
                // The fourteen former family-specific stones stay as item
                // definition tombstones on upgrade, but no longer carry a
                // material policy in the authoritative v9 manifest.
                EnhancementMaterials = ReplaceElementalMaterialPolicies(
                    prior.Policies.EnhancementMaterials)
            },
            prior.HolySuit);
    }

    private static IReadOnlyList<GearEnhancementMaterialDefinition>
        ReplaceElementalMaterialPolicies(
            IReadOnlyList<GearEnhancementMaterialDefinition> prior) =>
        prior.Where(static value => !IsElementalStoneRange(value.ItemId))
            .Concat(ReviewedEnhancementMaterials.Where(
                static value => IsElementalStoneRange(value.ItemId)))
            .OrderBy(static value => value.ItemId)
            .ToArray();

    private static bool IsElementalStoneRange(uint itemId) =>
        itemId is >= 16300 and <= 16320;

    private static async Task<V8PublicationSnapshot> ReadV8ShapeAsync(
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

    private static async Task VerifyPublishedV9ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState release,
        CancellationToken cancellationToken)
    {
        if (release.ManifestVersion != 9)
        {
            throw new InvalidOperationException(
                $"Expected item manifest v9, found {release.ManifestVersion}.");
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
                "failed manifest-v9 validation.");
        }
    }
}
