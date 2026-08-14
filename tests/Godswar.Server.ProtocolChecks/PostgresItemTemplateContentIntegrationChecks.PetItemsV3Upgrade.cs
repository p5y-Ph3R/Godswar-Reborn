using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private const string OfficialPetItemsV3Revision =
        "BCF91FCD7A9E3C5EA93B774143B5D2F9B714B147E40EBF0B85C639CF0DD63057";
    private const string OfficialPetItemsV3Source =
        "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
        "zephyr-v1+mount-speed-v3+pets-v3";

    private static async Task AssertOfficialPetItemsV3UpgradeAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var original = await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource);
        var focusBookIds = Enumerable.Range(10_530, 6).ToArray();
        var definitions = original.All
            .Where(item => !focusBookIds.Contains(checked((int)item.Id)))
            .ToDictionary(static item => item.Id);
        foreach (var tombstone in await BuildOfficialElementalTombstonesAsync(
                     dataSource))
        {
            definitions.TryAdd(tombstone.Id, tombstone);
        }
        var predecessor = definitions.Values.OrderBy(static item => item.Id)
            .ToArray();
        var computed = ComputeV9FixtureRevision(original, predecessor);
        Check.True(
            predecessor.Length == 1758 &&
            computed == OfficialPetItemsV3Revision,
            "reviewed pets-v3 fixture reproduces the exact live predecessor");

        try
        {
            var originalIds = original.All.Select(static item => item.Id)
                .ToHashSet();
            var additions = predecessor
                .Where(item => !originalIds.Contains(item.Id))
                .ToArray();
            await CreateAndPublishV9FixtureAsync(
                dataSource,
                originalRevision,
                computed,
                OfficialPetItemsV3Source,
                predecessor.Length,
                focusBookIds,
                additions);
            var predecessorFingerprint =
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    computed);

            var upgraded = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            await using var command = dataSource.CreateCommand("""
                SELECT release.entry_count, release.source,
                       (SELECT count(*)::integer
                        FROM item_template_content_definitions definition
                        WHERE definition.revision = publication.revision
                          AND definition.id = ANY(@focusBookIds))
                FROM item_template_content_publication publication
                JOIN item_template_content_revisions release
                  ON release.revision = publication.revision
                WHERE publication.family = 'items';
                """);
            command.Parameters.AddWithValue("focusBookIds", focusBookIds);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() &&
                reader.GetInt32(0) == 1764 &&
                reader.GetString(1).EndsWith(
                    "pets-v4",
                    StringComparison.Ordinal) &&
                reader.GetInt32(2) == 6,
                "exact pets-v3 upgrade publishes all six Focus books");
            Check.True(
                upgraded.Revision ==
                    "1851FC6EED26BC9DEDFAE2233479E1BCA6757392C5A7728DE68068B730C0D0AF" &&
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    computed) == predecessorFingerprint,
                "pets-v3 upgrade creates exact pets-v4 and preserves its predecessor");
        }
        finally
        {
            await RestoreItemPublicationAsync(dataSource, originalRevision);
        }
    }
}
