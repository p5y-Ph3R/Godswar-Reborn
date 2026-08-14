using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private const string OfficialPetItemsV2Revision =
        "28B1C5C6C2F292755B564CAC9D7C651CA821391C6D4E8C03EAE0D01535D60BB4";
    private const string OfficialPetItemsV2Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v2";
    private const string OfficialPetItemsV3Revision =
        "BCF91FCD7A9E3C5EA93B774143B5D2F9B714B147E40EBF0B85C639CF0DD63057";
    private const string OfficialPetItemsV3Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v3";

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
            ValidateSupportedPetItemsV9Predecessor(existing);
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
        definitions = await ReconcileReviewedPetItemsAsync(
            connection,
            transaction,
            definitions,
            cancellationToken);
        definitions = await ReplaceReviewedMountDefinitionsAsync(
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

    private static void ValidateSupportedPetItemsV9Predecessor(
        PublishedItemRevisionState release)
    {
        var isV2 = release.Revision.Equals(
                OfficialPetItemsV2Revision,
                StringComparison.Ordinal) &&
            release.Source.Equals(
                OfficialPetItemsV2Source,
                StringComparison.Ordinal);
        var isV3 = release.Revision.Equals(
                OfficialPetItemsV3Revision,
                StringComparison.Ordinal) &&
            release.Source.Equals(
                OfficialPetItemsV3Source,
                StringComparison.Ordinal);
        if (!isV2 && !isV3)
        {
            throw new InvalidOperationException(
                $"Manifest-v9 item revision {release.Revision} is not the " +
                "exact reviewed pets-v2/v3 predecessor; pet items were not " +
                "reconciled.");
        }
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
