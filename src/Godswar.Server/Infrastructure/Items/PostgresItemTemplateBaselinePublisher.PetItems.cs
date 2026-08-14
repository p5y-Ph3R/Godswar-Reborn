using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static readonly int[] PetItemCompatibilityItemIds =
        PetItemContentBaseline.ItemTemplates
            .Select(static value => value.Id)
            .OrderBy(static value => value)
            .ToArray();

    // Migration 061 published these stock identities before the immutable
    // item baseline reviewed them. Its generic consume-item projection used
    // equipment_slot=-1; the canonical baseline uses the repository-wide
    // consume-item value 0. Only that one exact legacy-field difference is
    // eligible for an in-place compatibility upgrade.
    private static readonly int[] LegacyPetItemSlotUpgradeIds =
        [10106, 10108, 11003, 11004];

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReconcileReviewedPetItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedPetItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            // Migration 061 creates the mutable FK identities. The immutable
            // publication owns runtime metadata, so replace any historical
            // definition and append identities absent from the prior release.
            byId[definition.Id] = definition;
        }

        return byId.Values
            .OrderBy(static value => value.Id)
            .ToArray();
    }

    private static async Task<bool> PublishedPetItemsAreCompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var expected = await ReadCanonicalReviewedPetItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var actual = await ReadPetItemRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            revision,
            cancellationToken);
        return actual.Count == expected.Count &&
            actual.Zip(expected).All(static pair =>
                DefinitionsEquivalent(pair.First, pair.Second));
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalReviewedPetItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seeds = PetItemContentBaseline.ItemTemplates
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
                "Reviewed pet-item JSON canonicalization was incomplete.");
        }

        return seeds.Select(seed => ToDefinition(seed) with
        {
            StatsJson = canonicalStats[seed.Id]
        })
            .ToArray();
    }

    private static async Task EnsurePetItemMutableTemplateCompatibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string publishedRevision,
        CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalReviewedPetItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var published = await ReadPetItemRowsAsync(
            connection,
            transaction,
            "item_template_content_definitions",
            publishedRevision,
            cancellationToken);
        ValidatePetItemRows(
            published,
            reviewed,
            $"published revision {publishedRevision}");

        await UpgradeExactLegacyPetItemSlotsAsync(
            connection,
            transaction,
            publishedRevision,
            cancellationToken);

        // character_items retains an FK to the mutable compatibility table.
        // Insert missing reviewed identities without overwriting local rows;
        // conflicting metadata fails closed during validation below.
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
                Value = PetItemCompatibilityItemIds
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadPetItemRowsAsync(
            connection,
            transaction,
            "item_templates",
            revision: null,
            cancellationToken);
        ValidatePetItemRows(
            mutable,
            published,
            "mutable item-template FK projection");
    }

    private static async Task UpgradeExactLegacyPetItemSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string publishedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.item_templates AS mutable
            SET equipment_slot = published.equipment_slot
            FROM public.item_template_content_definitions AS published
            WHERE published.revision = @revision
              AND mutable.id = published.id
              AND mutable.id = ANY(@itemIds)
              AND mutable.equipment_slot = -1
              AND published.equipment_slot = 0
              AND mutable.kind = published.kind
              AND mutable.name_key = published.name_key
              AND mutable.display_name = published.display_name
              AND mutable.class_ids = published.class_ids
              AND mutable.min_level IS NOT DISTINCT FROM published.min_level
              AND mutable.max_level IS NOT DISTINCT FROM published.max_level
              AND mutable.hand IS NOT DISTINCT FROM published.hand
              AND mutable.skill_flag IS NOT DISTINCT FROM published.skill_flag
              AND mutable.texture = published.texture
              AND mutable.icon = published.icon
              AND mutable.stats = published.stats;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", publishedRevision);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = LegacyPetItemSlotUpgradeIds
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadPetItemRowsAsync(
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
            Value = PetItemCompatibilityItemIds
        });

        var rows = new List<ItemTemplateDefinition>(
            PetItemCompatibilityItemIds.Length);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidatePetItemRows(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"Pet-item {source} contains {actual.Count} of " +
                $"{expected.Count} reviewed templates.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!DefinitionsEquivalent(actual[index], expected[index]))
            {
                throw new InvalidOperationException(
                    $"Pet item {expected[index].Id} conflicts with the " +
                    $"reviewed {source} definition.");
            }
        }
    }
}
