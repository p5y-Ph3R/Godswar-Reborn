using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private const string OfficialPetItemsV2Revision =
        "28B1C5C6C2F292755B564CAC9D7C651CA821391C6D4E8C03EAE0D01535D60BB4";
    private const string OfficialPetItemsV2Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v2";

    private static async Task AssertOfficialPetItemsV9UpgradeAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var original = await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource);
        var bookIds = PetSkillBookItemContentBaseline.ItemTemplates
            .Select(static item => item.Id)
            .Order()
            .ToArray();
        var tombstones = await BuildOfficialElementalTombstonesAsync(
            dataSource);
        var predecessor = original.All
            .Where(item => !bookIds.Contains(checked((int)item.Id)))
            .ToDictionary(static item => item.Id);
        foreach (var tombstone in tombstones)
        {
            predecessor.TryAdd(tombstone.Id, tombstone);
        }
        var definitions = predecessor.Values
            .OrderBy(static item => item.Id)
            .ToArray();
        var computed = ComputeV9FixtureRevision(original, definitions);
        Check.True(
            definitions.Length == 1734 &&
            computed == OfficialPetItemsV2Revision,
            "reviewed pets-v2 fixture reproduces the exact live predecessor");

        var originalIds = original.All
            .Select(static item => item.Id)
            .ToHashSet();
        var missingTombstones = tombstones
            .Where(item => !originalIds.Contains(item.Id))
            .ToArray();
        try
        {
            await CreateAndPublishV9FixtureAsync(
                dataSource,
                originalRevision,
                computed,
                OfficialPetItemsV2Source,
                definitions.Length,
                bookIds,
                missingTombstones);
            var predecessorFingerprint =
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    computed);

            var upgraded = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            await using var command = dataSource.CreateCommand("""
                SELECT release.entry_count,
                       (SELECT count(*)::integer
                        FROM item_template_content_definitions definition
                        WHERE definition.revision = publication.revision
                          AND definition.id = ANY(@bookIds)),
                       (SELECT count(*)::integer
                        FROM item_template_content_definitions definition
                        WHERE definition.revision = publication.revision
                          AND definition.id = ANY(@tombstoneIds))
                FROM item_template_content_publication publication
                JOIN item_template_content_revisions release
                  ON release.revision = publication.revision
                WHERE publication.family = 'items';
                """);
            command.Parameters.AddWithValue("bookIds", bookIds);
            command.Parameters.AddWithValue(
                "tombstoneIds",
                tombstones.Select(static item => checked((int)item.Id))
                    .ToArray());
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() &&
                reader.GetInt32(0) == 1764 &&
                reader.GetInt32(1) == 30 &&
                reader.GetInt32(2) == 14,
                "exact live pets-v2 upgrade retains 14 tombstones and adds 30 books");
            Check.True(
                upgraded.Revision != computed &&
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    computed) == predecessorFingerprint,
                "pets-v2 upgrade leaves its sealed predecessor immutable");

            var repeated = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.True(
                repeated.Revision == upgraded.Revision && !repeated.Created,
                "live-lineage pets-v4 publication is idempotent");
        }
        finally
        {
            await RestoreItemPublicationAsync(dataSource, originalRevision);
        }
    }

    private static async Task AssertUnreviewedPetItemsV9RejectedAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var original = await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource);
        var removedId = PetSkillBookItemContentBaseline.ItemTemplates[0].Id;
        var definitions = original.All
            .Where(item => item.Id != checked((uint)removedId))
            .ToArray();
        var revision = ComputeV9FixtureRevision(original, definitions);
        Check.True(
            revision != OfficialPetItemsV2Revision,
            "unreviewed v9 fixture does not impersonate the supported predecessor");
        try
        {
            await CreateAndPublishV9FixtureAsync(
                dataSource,
                originalRevision,
                revision,
                "unreviewed-v9-pet-reconciliation-test",
                definitions.Length,
                [removedId],
                []);
            try
            {
                _ = await PostgresItemTemplateBaselinePublisher
                    .EnsurePublishedAsync(dataSource);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains(
                    "exact reviewed pets-v2/v3 predecessor",
                    StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                "A hash-valid but unreviewed manifest-v9 was reconciled.");
        }
        finally
        {
            await RestoreItemPublicationAsync(dataSource, originalRevision);
        }
    }

    private static string ComputeV9FixtureRevision(
        PinnedItemTemplateCatalog catalog,
        IReadOnlyList<ItemTemplateDefinition> definitions) =>
        ItemTemplateContentRevisionHasher.ComputeV6(
            definitions,
            catalog.Attributes,
            catalog.EquipmentRanks,
            catalog.HolySuitEffects,
            catalog.Materials.ForgingMaterials,
            catalog.Materials.GearEnhancementMaterials,
            catalog.Materials.AttributeDusts,
            catalog.Materials.GearMentorRecipes,
            catalog.HolySuit.Tiers,
            catalog.HolySuit.Upgrades,
            catalog.HolySuit.Consumables,
            catalog.HolySuit.OperationPolicy ??
                throw new InvalidOperationException(
                    "The v9 fixture has no Holy Suit operation policy."));
}
