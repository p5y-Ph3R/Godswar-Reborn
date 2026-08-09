using System.Globalization;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertMountSpeedPublicationAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT stats->>'Speed'
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id = 16204;
            """);
        command.Parameters.AddWithValue("revision", revision);
        var speedText = (string?)await command.ExecuteScalarAsync();
        var speeds = speedText?
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => decimal.Parse(
                value,
                CultureInfo.InvariantCulture))
            .ToArray() ?? [];
        Check.True(
            speeds.Length == 20 &&
            speeds[0] == 0.24m &&
            speeds[9] == 0.34m &&
            speeds[19] == 0.54m,
            "published Erebus mount pins the reviewed additive Speed curve");

        var repeat = await PostgresItemTemplateBaselinePublisher
            .EnsurePublishedAsync(dataSource);
        Check.Equal(
            revision,
            repeat.Revision,
            "reviewed mount Speed publication is idempotent");
    }
}
