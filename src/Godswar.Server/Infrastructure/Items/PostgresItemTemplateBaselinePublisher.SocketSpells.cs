using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        AppendMissingReviewedSocketSpellsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedSocketSpellsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            if (byId.TryGetValue(definition.Id, out var existing))
            {
                if (!DefinitionsEquivalent(existing, definition))
                {
                    throw new InvalidOperationException(
                        $"Reviewed Socket Spell {definition.Id} conflicts " +
                        "with the published item definition.");
                }
                continue;
            }

            byId.Add(definition.Id, definition);
        }

        return byId.Values
            .OrderBy(static value => value.Id)
            .ToArray();
    }

    private static async Task<bool>
        PublishedSocketSpellItemsAreCompleteAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var expected = await ReadCanonicalReviewedSocketSpellsAsync(
            connection,
            transaction,
            cancellationToken);
        var actual = new List<ItemTemplateDefinition>(expected.Count);
        await using var command = new NpgsqlCommand("""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id BETWEEN @firstItemId AND @lastItemId
            ORDER BY id;
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue(
            "firstItemId",
            checked((int)SocketSpellItemContentBaseline.FirstItemId));
        command.Parameters.AddWithValue(
            "lastItemId",
            checked((int)SocketSpellItemContentBaseline.LastItemId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            actual.Add(ReadDefinition(reader));
        }

        return actual.Count == expected.Count &&
            actual.Zip(expected)
                .All(static pair =>
                    DefinitionsEquivalent(pair.First, pair.Second));
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedSocketSpellsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = SocketSpellItemContentBaseline.ItemTemplates
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
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            canonicalStats.Add(reader.GetInt32(0), reader.GetString(1));
        }

        if (canonicalStats.Count != seeds.Length)
        {
            throw new InvalidDataException(
                "Reviewed Socket Spell JSON canonicalization was incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
            {
                StatsJson = canonicalStats[seed.Id]
            })
            .ToArray();
    }
}
