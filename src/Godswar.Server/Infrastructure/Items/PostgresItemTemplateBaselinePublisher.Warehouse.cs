using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReconcileReviewedWarehouseItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<ItemTemplateDefinition> prior,
            CancellationToken cancellationToken)
    {
        var reviewed = await ReadCanonicalWarehouseItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var byId = prior.ToDictionary(static value => value.Id);
        foreach (var definition in reviewed)
        {
            byId[definition.Id] = definition;
        }
        return byId.Values.OrderBy(static value => value.Id).ToArray();
    }

    private static async Task<bool> PublishedWarehouseItemsAreCompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var expected = await ReadCanonicalWarehouseItemsAsync(
            connection,
            transaction,
            cancellationToken);
        var actual = await ReadWarehouseItemsAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        return actual.Count == expected.Count &&
            actual.Zip(expected).All(static pair =>
                DefinitionsEquivalent(pair.First, pair.Second));
    }

    private static async Task EnsureWarehouseMutableCompatibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var published = await ReadWarehouseItemsAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var expected = await ReadCanonicalWarehouseItemsAsync(
            connection,
            transaction,
            cancellationToken);
        ValidateWarehouseItems(published, expected, "published revision");

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO item_templates (
                id, kind, name_key, display_name, equipment_slot, class_ids,
                min_level, max_level, hand, skill_flag, texture, icon, stats)
            SELECT id, kind, name_key, display_name, equipment_slot, class_ids,
                   min_level, max_level, hand, skill_flag, texture, icon, stats
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id = @itemId
            ON CONFLICT (id) DO NOTHING;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue(
                "itemId",
                WarehouseItemContentBaseline.StorageBoxKeyItemId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var mutable = await ReadWarehouseItemsAsync(
            connection,
            transaction,
            revision: null,
            cancellationToken);
        ValidateWarehouseItems(mutable, expected, "mutable FK projection");
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadCanonicalWarehouseItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var seed = WarehouseItemContentBaseline.ItemTemplates.Single();
        await using var command = new NpgsqlCommand(
            "SELECT @stats::jsonb::text;",
            connection,
            transaction);
        command.Parameters.AddWithValue("stats", seed.StatsJson);
        var canonical = await command.ExecuteScalarAsync(cancellationToken)
            as string ?? throw new InvalidDataException(
                "Warehouse item JSON canonicalization failed.");
        return [ToDefinition(seed) with { StatsJson = canonical }];
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadWarehouseItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string? revision,
            CancellationToken cancellationToken)
    {
        var table = revision is null
            ? "item_templates"
            : "item_template_content_definitions";
        var revisionPredicate = revision is null
            ? string.Empty
            : "revision = @revision AND ";
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM {table}
            WHERE {revisionPredicate}id = @itemId
            ORDER BY id;
            """,
            connection,
            transaction);
        if (revision is not null)
        {
            command.Parameters.AddWithValue("revision", revision);
        }
        command.Parameters.AddWithValue(
            "itemId",
            WarehouseItemContentBaseline.StorageBoxKeyItemId);
        var rows = new List<ItemTemplateDefinition>(1);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }

    private static void ValidateWarehouseItems(
        IReadOnlyList<ItemTemplateDefinition> actual,
        IReadOnlyList<ItemTemplateDefinition> expected,
        string source)
    {
        if (actual.Count != 1 || expected.Count != 1 ||
            !DefinitionsEquivalent(actual[0], expected[0]))
        {
            throw new InvalidOperationException(
                $"Storage Box Key conflicts with the reviewed {source} definition.");
        }
    }
}
