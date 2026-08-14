using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private const string ElementalStoneTexture =
        "./Localization/en_us/UI/Texture/Icon2.gwo";

    private static async Task ValidateOfficialV8ElementalReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var expectedMaterials = BuildOfficialV8ElementalMaterials();
        var expectedDefinitions =
            await CanonicalizeOfficialV8ElementalDefinitionsAsync(
                connection,
                transaction,
                expectedMaterials,
                cancellationToken);
        var actualDefinitions = await ReadV8ElementalDefinitionsAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var policies = await ReadPublishedPoliciesAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var actualMaterials = policies.EnhancementMaterials
            .Where(static value => value.ItemId is >= 16300 and <= 16320)
            .OrderBy(static value => value.ItemId)
            .ToArray();

        if (actualDefinitions.Count != expectedDefinitions.Count ||
            actualMaterials.Length != expectedMaterials.Count)
        {
            throw new InvalidOperationException(
                "Manifest-v8 does not contain the complete reviewed " +
                "21-stone elemental identity set.");
        }

        for (var index = 0; index < expectedDefinitions.Count; index++)
        {
            if (!DefinitionsEquivalent(
                    actualDefinitions[index],
                    expectedDefinitions[index]) ||
                !ElementalMaterialDefinitionsEqual(
                    actualMaterials[index],
                    expectedMaterials[index]))
            {
                throw new InvalidOperationException(
                    $"Manifest-v8 elemental stone " +
                    $"{expectedDefinitions[index].Id} is not an exact " +
                    "reviewed identity; mutable content was not migrated.");
            }
        }
    }

    internal static IReadOnlyList<GearEnhancementMaterialDefinition>
        BuildOfficialV8ElementalMaterials()
    {
        var materials = new List<GearEnhancementMaterialDefinition>(21);
        foreach (var element in Enum.GetValues<ElementKind>())
        {
            foreach (var family in Enum.GetValues<ElementalStatFamily>())
            {
                var ordinal = ((int)element * 3) + (int)family;
                var attributeId = 480 + ordinal;
                materials.Add(new GearEnhancementMaterialDefinition(
                    checked((uint)(16300 + ordinal)),
                    $"ElementalMaterial{attributeId}",
                    $"{element} {family} Stone",
                    GearEnhancementMaterialKind.AttributeStone,
                    ElementalStoneTexture,
                    $"{648 + ((int)element * 36)}," +
                        $"{180 + ((int)family * 36)}",
                    GearEnhancementMaterialCatalog.ShippedStackCap,
                    Random: 0,
                    Distribution: "0,0",
                    AttributeName: $"{element} {family}",
                    AttributeChain: [attributeId]));
            }
        }
        return materials;
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        CanonicalizeOfficialV8ElementalDefinitionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<GearEnhancementMaterialDefinition> materials,
            CancellationToken cancellationToken)
    {
        var seeds = materials
            .Select(static value => value.ToItemTemplateSeed())
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
                "Manifest-v8 elemental JSON canonicalization was incomplete.");
        }
        return seeds.Select(seed => ToDefinition(seed) with
            {
                StatsJson = canonical[seed.Id]
            })
            .ToArray();
    }

    private static async Task<IReadOnlyList<ItemTemplateDefinition>>
        ReadV8ElementalDefinitionsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var rows = new List<ItemTemplateDefinition>(21);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id BETWEEN 16300 AND 16320
            ORDER BY id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadDefinition(reader));
        }
        return rows;
    }
}
