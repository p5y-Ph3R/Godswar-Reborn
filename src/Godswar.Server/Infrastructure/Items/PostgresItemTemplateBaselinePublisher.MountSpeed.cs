using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task<bool> PublishedMountSpeedProfileIsCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var published = (await ReadPublishedDefinitionsAsync(
                connection,
                transaction,
                revision,
                cancellationToken))
            .Where(IsMountDefinition)
            .OrderBy(static value => value.Id)
            .ToArray();
        var reviewed = await ReadCanonicalReviewedMountDefinitionsAsync(
            connection,
            transaction,
            cancellationToken);
        return published.Length == reviewed.Count &&
               published.Zip(reviewed).All(static pair =>
                   DefinitionsEquivalent(pair.First, pair.Second));
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReplaceReviewedMountDefinitionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedMountDefinitionsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            if (byId.TryGetValue(definition.Id, out var existing) &&
                !IsMountDefinition(existing))
            {
                throw new InvalidOperationException(
                    $"Reviewed mount {definition.Id} conflicts with " +
                    $"published kind '{existing.Kind}'.");
            }

            byId[definition.Id] = definition;
        }

        return byId.Values.OrderBy(static value => value.Id).ToArray();
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedMountDefinitionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = ReviewedItemTemplateSeeds()
            .Where(static value => value.Kind.Equals(
                "mount",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static value => value.Id)
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
            Value = seeds.Select(static value => value.Id).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter(
            "statsJson",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = seeds.Select(static value => value.StatsJson).ToArray()
        });

        var canonicalStats = new Dictionary<int, string>(seeds.Length);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            canonicalStats.Add(reader.GetInt32(0), reader.GetString(1));
        }
        if (canonicalStats.Count != seeds.Length)
        {
            throw new InvalidDataException(
                "Reviewed mount JSON canonicalization was incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
            {
                StatsJson = canonicalStats[seed.Id]
            })
            .ToArray();
    }

    private static bool IsMountDefinition(ItemTemplateDefinition definition) =>
        definition.Kind.Equals("mount", StringComparison.OrdinalIgnoreCase);
}
