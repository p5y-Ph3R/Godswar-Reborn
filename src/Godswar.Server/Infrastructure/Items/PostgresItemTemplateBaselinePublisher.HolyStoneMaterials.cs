using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static readonly int[] HolyStoneMaterialCompatibilityItemIds =
        HolyStoneMaterialItemContentBaseline.ItemTemplates
            .Select(static value => value.Id)
            .OrderBy(static value => value)
            .ToArray();

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReconcileReviewedHolyStoneMaterialsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedHolyStoneMaterialsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            // Migration 029 published a deliberately incomplete subset with
            // legacy metadata. Revision 058 is the reviewed reconciliation,
            // so replace those entries and append the missing client items.
            byId[definition.Id] = definition;
        }

        return byId.Values
            .OrderBy(static value => value.Id)
            .ToArray();
    }

    private static async Task<bool>
        PublishedHolyStoneMaterialsAreCompleteAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var expected = await ReadCanonicalReviewedHolyStoneMaterialsAsync(
            connection,
            transaction,
            cancellationToken);
        var itemIds = expected
            .Select(static value => checked((int)value.Id))
            .ToArray();
        var actual = new List<ItemTemplateDefinition>(expected.Count);
        await using var command = new NpgsqlCommand("""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id = ANY(@itemIds)
            ORDER BY id;
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = itemIds
        });
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            actual.Add(ReadDefinition(reader));
        }

        return actual.Count == expected.Count &&
            actual.Zip(expected).All(static pair =>
                DefinitionsEquivalent(pair.First, pair.Second));
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedHolyStoneMaterialsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = HolyStoneMaterialItemContentBaseline.ItemTemplates
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
                "Reviewed Holy Stone material JSON canonicalization was " +
                "incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
            {
                StatsJson = canonicalStats[seed.Id]
            })
            .ToArray();
    }

    private static async Task
        EnsureHolyStoneMutableTemplateCompatibilityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string publishedRevision,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedHolyStoneMaterialsAsync(
            connection,
            transaction,
            cancellationToken);
        var published = await ReadHolyStoneMaterialRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            publishedRevision,
            cancellationToken);
        ValidateHolyStoneMaterialRows(
            published,
            reviewed,
            $"published revision {publishedRevision}");

        // character_items retains an FK to the mutable compatibility table.
        // Insert missing reviewed identities without overwriting a local row;
        // the validation below fails closed if an existing row conflicts.
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO item_templates (
                id, kind, name_key, display_name, equipment_slot, class_ids,
                min_level, max_level, hand, skill_flag, texture, icon, stats)
            SELECT id, kind, name_key, display_name, equipment_slot, class_ids,
                   min_level, max_level, hand, skill_flag, texture, icon, stats
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id = ANY(@itemIds)
            ORDER BY id
            ON CONFLICT (id) DO NOTHING;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("revision", publishedRevision);
            command.Parameters.Add(new NpgsqlParameter(
                "itemIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = HolyStoneMaterialCompatibilityItemIds
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadHolyStoneMaterialRowsAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        ValidateHolyStoneMaterialRows(
            mutable,
            published,
            "mutable item-template FK projection");
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadHolyStoneMaterialRowsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string table,
            string? revision,
            CancellationToken cancellationToken)
    {
        var revisionPredicate = revision is null
            ? string.Empty
            : "revision = @revision AND ";
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM {table}
            WHERE {revisionPredicate}id = ANY(@itemIds)
            ORDER BY id;
            """,
            connection,
            transaction);
        if (revision is not null)
        {
            command.Parameters.AddWithValue("revision", revision);
        }
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = HolyStoneMaterialCompatibilityItemIds
        });

        var rows = new List<ItemTemplateDefinition>(
            HolyStoneMaterialCompatibilityItemIds.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidateHolyStoneMaterialRows(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Holy Stone material {source} contains {actual.Count} of " +
                $"{expected.Count} reviewed templates.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!DefinitionsEquivalent(actual[index], expected[index]))
            {
                throw new InvalidOperationException(
                    $"Holy Stone material {expected[index].Id} conflicts " +
                    $"with the reviewed {source} definition.");
            }
        }
    }
}
