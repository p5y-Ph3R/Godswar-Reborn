using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    // This is intentionally fixed to the reviewed manifest-v5 Holy Suit set.
    // It is a mutable FK-identity compatibility projection, not runtime
    // content authority. A future manifest must make an explicit decision
    // before broadening this repair boundary.
    private static readonly int[] HolySuitMutableCompatibilityItemIds =
    [
        9010, 9011, 9012, 9013, 9014, 9015, 9016,
        9020, 9021, 9022, 9023, 9024, 9025
    ];

    private static async Task
        EnsureHolySuitMutableTemplateCompatibilityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string publishedRevision,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedHolySuitItemsAsync(
            connection,
            transaction,
            cancellationToken);
        ValidateReviewedCompatibilitySet(reviewed);

        var published = await ReadHolySuitCompatibilityRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            publishedRevision,
            cancellationToken);
        ValidateRowsMatchReviewed(
            published,
            reviewed,
            $"published revision {publishedRevision}");

        // Insert only missing FK identity rows. Existing mutable rows are
        // never overwritten; the validation below fails the transaction if
        // any existing identity conflicts with the reviewed publication.
        await using (var command = new NpgsqlCommand("""
            INSERT INTO item_templates (
                id, kind, name_key, display_name, equipment_slot, class_ids,
                min_level, max_level, hand, skill_flag, texture, icon, stats)
            SELECT id, kind, name_key, display_name, equipment_slot, class_ids,
                   min_level, max_level, hand, skill_flag, texture, icon, stats
            FROM item_template_content_definitions
            WHERE revision = @revision AND id = ANY(@itemIds)
            ORDER BY id
            ON CONFLICT (id) DO NOTHING;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision", publishedRevision);
            command.Parameters.Add(new NpgsqlParameter(
                "itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = HolySuitMutableCompatibilityItemIds
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadHolySuitCompatibilityRowsAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        ValidateRowsMatchReviewed(
            mutable,
            published,
            "mutable item-template FK identity projection");
    }

    private static void ValidateReviewedCompatibilitySet(
        IReadOnlyList<ItemTemplateDefinition> reviewed)
    {
        var ids = reviewed.Select(static value => checked((int)value.Id))
            .OrderBy(static value => value)
            .ToArray();
        if (!ids.SequenceEqual(HolySuitMutableCompatibilityItemIds))
        {
            throw new InvalidOperationException(
                "Reviewed Holy Suit item set does not match the explicit " +
                "manifest-v5 mutable compatibility boundary.");
        }
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadHolySuitCompatibilityRowsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string table,
            string? revision,
            CancellationToken cancellationToken)
    {
        var revisionPredicate = revision is null
            ? string.Empty
            : "revision = @revision AND ";
        await using var command = new NpgsqlCommand($"""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM {table}
            WHERE {revisionPredicate}id = ANY(@itemIds)
            ORDER BY id;
            """, connection, transaction);
        if (revision is not null)
        {
            command.Parameters.AddWithValue("revision", revision);
        }
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = HolySuitMutableCompatibilityItemIds
        });

        var rows = new List<ItemTemplateDefinition>(
            HolySuitMutableCompatibilityItemIds.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidateRowsMatchReviewed(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Holy Suit {source} contains {actual.Count} of " +
                $"{expected.Count} required item templates.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!DefinitionsEquivalent(actual[index], expected[index]))
            {
                throw new InvalidOperationException(
                    $"Holy Suit item {expected[index].Id} conflicts with " +
                    $"the reviewed {source} definition; no mutable row " +
                    "was overwritten.");
            }
        }
    }
}
