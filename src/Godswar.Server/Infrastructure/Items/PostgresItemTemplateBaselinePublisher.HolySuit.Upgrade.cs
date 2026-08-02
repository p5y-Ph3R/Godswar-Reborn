using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private sealed record HolySuitPolicySnapshot(
        IReadOnlyList<HolySuitTierDefinition> Tiers,
        IReadOnlyList<HolySuitUpgradeDefinition> Upgrades,
        IReadOnlyList<HolySuitConsumableDefinition> Consumables,
        HolySuitOperationPolicy OperationPolicy);

    private sealed record V5PublicationSnapshot(
        IReadOnlyList<ItemTemplateDefinition> Definitions,
        ItemPolicySnapshot Policies,
        HolySuitPolicySnapshot HolySuit);

    private static HolySuitPolicySnapshot ReviewedHolySuitPolicy { get; } =
        new(
            HolySuitContentBaseline.Tiers,
            HolySuitContentBaseline.Upgrades,
            HolySuitContentBaseline.Consumables,
            HolySuitContentBaseline.OperationPolicy);

    private static async Task<V5PublicationSnapshot> PrepareV5PublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublishedItemRevisionState? existing,
        CancellationToken cancellationToken)
    {
        if (existing is { ManifestVersion: 5 })
        {
            var publishedHolySuit =
                await ReadPublishedHolySuitPoliciesAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken);
            return new V5PublicationSnapshot(
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
                    publishedHolySuit.Tiers,
                    publishedHolySuit.Upgrades,
                    publishedHolySuit.Consumables,
                    ReviewedHolySuitPolicy.OperationPolicy));
        }

        V4PublicationSnapshot prior;
        if (existing is { ManifestVersion: 4 })
        {
            prior = new V4PublicationSnapshot(
                await ReadPublishedDefinitionsAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken),
                await ReadPublishedPoliciesAsync(
                    connection,
                    transaction,
                    existing.Revision,
                    cancellationToken));
            ValidateV4Publication(existing, prior);
        }
        else
        {
            prior = await PrepareV4PublicationAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
        }

        return new V5PublicationSnapshot(
            await AppendMissingReviewedHolySuitItemsAsync(
                connection,
                transaction,
                prior.Definitions,
                cancellationToken),
            prior.Policies,
            ReviewedHolySuitPolicy);
    }

    private static void ValidateV4Publication(
        PublishedItemRevisionState release,
        V4PublicationSnapshot snapshot)
    {
        var policies = snapshot.Policies;
        if (release.EntryCount != snapshot.Definitions.Count ||
            release.AttributeCount != policies.Attributes.Count ||
            release.EquipmentRankCount != policies.EquipmentRanks.Count ||
            release.HolySuitEffectCount != policies.HolySuitEffects.Count ||
            release.MaterialPolicyCount != policies.MaterialPolicyCount ||
            release.MaterialRecipeCount != policies.MaterialRecipeCount ||
            release.HolySuitTierCount != 0 ||
            release.HolySuitUpgradeCount != 0 ||
            release.HolySuitConsumableCount != 0 ||
            release.HolySuitPolicyCount != 0 ||
            !ItemTemplateContentRevisionHasher.Compute(
                    snapshot.Definitions,
                    policies.Attributes,
                    policies.EquipmentRanks,
                    policies.HolySuitEffects,
                    policies.ForgingMaterials,
                    policies.EnhancementMaterials,
                    policies.AttributeDusts,
                    policies.Recipes)
                .Equals(release.Revision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Version-4 item-content revision {release.Revision} " +
                "failed canonical count or hash validation.");
        }
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        AppendMissingReviewedHolySuitItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewedItems = await ReadCanonicalReviewedHolySuitItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var reviewed in reviewedItems)
        {
            if (byId.TryGetValue(reviewed.Id, out var existing))
            {
                if (!DefinitionsEquivalent(existing, reviewed))
                {
                    throw new InvalidOperationException(
                        $"Reviewed Holy Suit item {reviewed.Id} conflicts " +
                        "with the published item definition.");
                }
                continue;
            }

            byId.Add(reviewed.Id, reviewed);
        }

        return byId.Values.OrderBy(static value => value.Id).ToArray();
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedHolySuitItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = HolySuitContentBaseline.ItemTemplates
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
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            canonical.Add(reader.GetInt32(0), reader.GetString(1));
        }

        if (canonical.Count != seeds.Length)
        {
            throw new InvalidDataException(
                "Reviewed Holy Suit JSON canonicalization was incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
            {
                StatsJson = canonical[seed.Id]
            })
            .ToArray();
    }
}
