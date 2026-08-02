using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static readonly int[] ClassSuitCompatibilityItemIds =
        ClassSuitItemContentBaseline.PromotionalInsignias
            .Select(static value => value.Id)
            .OrderBy(static value => value)
            .ToArray();

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        AppendMissingReviewedClassSuitItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedClassSuitItemsAsync(
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
                        $"Reviewed Class Suit item {definition.Id} " +
                        "conflicts with the published item definition.");
                }
                continue;
            }

            byId.Add(definition.Id, definition);
        }

        return byId.Values.OrderBy(static value => value.Id).ToArray();
    }

    private static async Task<bool>
        PublishedClassSuitItemsAreCompleteAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string publishedRevision,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedClassSuitItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var published = await ReadClassSuitCompatibilityRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            publishedRevision,
            cancellationToken);
        if (published.Count != reviewed.Count)
        {
            return false;
        }

        ValidateClassSuitRowsMatchReviewed(
            published,
            reviewed,
            $"published revision {publishedRevision}");
        return true;
    }

    private static async Task
        EnsureClassSuitMutableTemplateCompatibilityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string publishedRevision,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedClassSuitItemsAsync(
            connection,
            transaction,
            cancellationToken);
        ValidateClassSuitReviewedSet(reviewed);
        var published = await ReadClassSuitCompatibilityRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            publishedRevision,
            cancellationToken);
        ValidateClassSuitRowsMatchReviewed(
            published,
            reviewed,
            $"published revision {publishedRevision}");

        // character_items still has an FK to the mutable staging table.
        // Project only missing reviewed identities; never overwrite an
        // existing conflicting mutable row.
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
            command.Parameters.AddWithValue(
                "revision",
                publishedRevision);
            command.Parameters.Add(new NpgsqlParameter(
                "itemIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = ClassSuitCompatibilityItemIds
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadClassSuitCompatibilityRowsAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        ValidateClassSuitRowsMatchReviewed(
            mutable,
            published,
            "mutable item-template FK identity projection");
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedClassSuitItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = ClassSuitItemContentBaseline.PromotionalInsignias
            .OrderBy(static value => value.Id)
            .ToArray();
        await using var command = new NpgsqlCommand(
            """
            SELECT input.item_id, input.stats::jsonb::text
            FROM unnest(@itemIds, @statsJson) AS input(item_id, stats)
            ORDER BY input.item_id;
            """,
            connection,
            transaction);
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
                "Reviewed Class Suit JSON canonicalization was incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
            {
                StatsJson = canonical[seed.Id]
            })
            .ToArray();
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadClassSuitCompatibilityRowsAsync(
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
            Value = ClassSuitCompatibilityItemIds
        });

        var rows = new List<ItemTemplateDefinition>(
            ClassSuitCompatibilityItemIds.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidateClassSuitReviewedSet(
        IReadOnlyList<ItemTemplateDefinition> reviewed)
    {
        var ids = reviewed.Select(static value => checked((int)value.Id))
            .OrderBy(static value => value)
            .ToArray();
        if (!ids.SequenceEqual(ClassSuitCompatibilityItemIds))
        {
            throw new InvalidOperationException(
                "Reviewed Class Suit item set does not match its explicit " +
                "mutable compatibility boundary.");
        }
    }

    private static void ValidateClassSuitRowsMatchReviewed(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Class Suit {source} contains {actual.Count} of " +
                $"{expected.Count} required item templates.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!DefinitionsEquivalent(actual[index], expected[index]))
            {
                throw new InvalidOperationException(
                    $"Class Suit item {expected[index].Id} conflicts with " +
                    $"the reviewed {source} definition; no mutable row " +
                    "was overwritten.");
            }
        }
    }
}
