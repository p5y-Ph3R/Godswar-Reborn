using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static readonly int[] ElementalStoneCompatibilityItemIds =
        ElementalAttributeCatalog.All
            .Select(static value => checked((int)value.StoneItemId))
            .OrderBy(static value => value)
            .ToArray();

    private static async Task
        EnsureElementalMutableTemplateCompatibilityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string publishedRevision,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedElementalStoneItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var published = await ReadElementalStoneTemplatesAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            publishedRevision,
            cancellationToken);
        ValidateElementalStoneTemplates(
            published,
            reviewed,
            $"published revision {publishedRevision}");

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
                Value = ElementalStoneCompatibilityItemIds
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadElementalStoneTemplatesAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        ValidateElementalStoneTemplates(
            mutable,
            reviewed,
            "mutable item-template FK projection");
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedElementalStoneItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = ElementalAttributeCatalog.All
            .Select(static definition =>
            {
                if (!GearEnhancementMaterialCatalog.TryGet(
                        definition.StoneItemId,
                        out var material))
                {
                    throw new InvalidOperationException(
                        $"Elemental stone {definition.StoneItemId} is not reviewed.");
                }
                return material.ToItemTemplateSeed();
            })
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
        var canonical = new Dictionary<int, string>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            canonical.Add(reader.GetInt32(0), reader.GetString(1));
        }
        if (canonical.Count != seeds.Length)
        {
            throw new InvalidDataException(
                "Elemental stone JSON canonicalization was incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
            {
                StatsJson = canonical[seed.Id]
            })
            .ToArray();
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadElementalStoneTemplatesAsync(
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
            Value = ElementalStoneCompatibilityItemIds
        });

        var rows = new List<ItemTemplateDefinition>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidateElementalStoneTemplates(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Elemental stone {source} contains {actual.Count} of " +
                $"{expected.Count} reviewed templates.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!DefinitionsEquivalent(actual[index], expected[index]))
            {
                throw new InvalidOperationException(
                    $"Elemental stone {expected[index].Id} conflicts with " +
                    $"the reviewed {source} definition.");
            }
        }
    }
}
