using System.Data;
using System.Globalization;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGameplayV3UpgradeIntegrationChecks
{
    private const string LegacyV2Revision =
        "897AB928AE2AA8168E39D1A8909D35349ED5FFE337D2C5C132005CC81C71CA62";
    private const string InflatedV3Revision =
        "23D4689494C17F34DDA8C6242520CA2A67169311B478E41DA3172DB3308DF5AA";

    private static readonly IReadOnlyDictionary<int, decimal> ChampionStock =
        new Dictionary<int, decimal>
        {
            [50] = 3m,
            [51] = 10m,
            [52] = 9m,
            [53] = 50m,
            [54] = 2m,
            [55] = 0.005m,
            [56] = 5m,
            [57] = 16m,
            [58] = 4m,
            [59] = 7m,
            [60] = 3m,
            [61] = 0.01m,
            [62] = 20m,
            [63] = 1.6m,
            [64] = 4m,
            [65] = 1.2m,
            [66] = 7m,
            [67] = 90m,
            [68] = 90m
        };

    private static async Task InflateMutableChampionTalentsAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH correction(id, effect_value, tooltip_value) AS (
                VALUES
                    (50, 3::numeric, 7.8::numeric),
                    (51, 10::numeric, 26::numeric),
                    (52, 9::numeric, 23.4::numeric),
                    (53, 50::numeric, 130::numeric),
                    (54, 2::numeric, 5.2::numeric),
                    (55, 0.005::numeric, 0.013::numeric),
                    (56, 5::numeric, 13::numeric),
                    (57, 16::numeric, 41.6::numeric),
                    (58, 4::numeric, 10.4::numeric),
                    (59, 7::numeric, 18.2::numeric),
                    (60, 3::numeric, 7.8::numeric),
                    (61, 0.01::numeric, 0.026::numeric),
                    (62, 20::numeric, 52::numeric),
                    (63, 1.6::numeric, 4.16::numeric),
                    (64, 4::numeric, 10.4::numeric),
                    (65, 1.2::numeric, 3.12::numeric),
                    (66, 7::numeric, 18.2::numeric),
                    (67, 90::numeric, 234::numeric),
                    (68, 90::numeric, 234::numeric)
            )
            UPDATE talent_templates talent
            SET effect_value = correction.tooltip_value,
                stats = jsonb_set(
                    talent.stats,
                    ARRAY[talent.effect_type],
                    to_jsonb((talent.effect_id::text || ',' ||
                        correction.tooltip_value::text)::text),
                    false)
            FROM correction
            WHERE talent.id = correction.id
              AND talent.class_id = 1;
            """,
            connection);
        Check.Equal(
            ChampionStock.Count,
            await command.ExecuteNonQueryAsync(),
            "the integration fixture restores all 19 tooltip scalars");
    }

    private static async Task AssertCleanAndDirectPublicationPathsAsync(
        NpgsqlDataSource dataSource,
        string connectionString)
    {
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(
                         IsolationLevel.RepeatableRead))
        {
            var clean = await PostgresGameplayContentPublisher
                .EnsurePublishedAsync(connection, transaction);
            Check.True(
                clean.Created && clean.Publisher == LegacyPublisher,
                "a clean no-publication database seals corrected authority");
            var content = await PostgresWorldContentReaderLoader
                .LoadPublishedGameplayContentAsync(
                    connection,
                    transaction,
                    CancellationToken.None);
            AssertChampionCatalog(content.Talents, inflated: false);
            await transaction.RollbackAsync();
        }
        Check.Equal(
            0,
            await CountGameplayRevisionsAsync(dataSource),
            "the clean-publication probe rolls back without immutable deletes");

        await InflateMutableChampionTalentsAsync(dataSource);
        var direct = await PostgresGameplayContentPublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(
            direct.Created &&
            direct.Publisher == LegacyPublisher &&
            direct.Revision == InflatedV3Revision,
            "the fixture creates the exact historical direct-v3 publication " +
            $"(actual {direct.Revision}, {direct.Publisher})");
        await AssertChampionVectorAsync(
            dataSource,
            direct.Revision,
            inflated: true,
            mutable: false);

        await AddMutableMarkerAndPoisonAsync(dataSource);
        WorldContentUnavailableException? rejection = null;
        try
        {
            _ = await PostgresGameplayContentPublisher
                .EnsurePublishedAsync(connectionString);
        }
        catch (WorldContentUnavailableException error)
        {
            rejection = error;
        }
        Check.True(
            rejection?.Reason == WorldContentFailureReason.RevisionMismatch,
            "poison rejects a fresh direct-v3 authority successor");
        Check.Equal(
            1,
            await CountGameplayRevisionsAsync(dataSource),
            "failed fresh successor insertion and copies roll back atomically");
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            Check.Equal(
                InflatedV3Revision,
                await ReadCurrentRevisionAsync(connection),
                "failed fresh successor leaves the direct-v3 pointer unchanged");
        }

        await InflateMutableChampionTalentsAsync(dataSource);
        var corrected = await PostgresGameplayContentPublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(
            corrected.Created && corrected.Publisher == AuthorityPublisher,
            "restoring mutable authority permits the fresh successor");
        Check.Equal(
            2,
            await CountGameplayRevisionsAsync(dataSource),
            "successful direct-v3 upgrade retains predecessor and successor");
        await AssertChampionVectorAsync(
            dataSource,
            corrected.Revision,
            inflated: false,
            mutable: false);
        await AssertMutableMarkerPreservedAsync(dataSource);
        await RemoveMutableMarkerAsync(dataSource);
    }

    private static async Task AssertDirectV3AuthorityUpgradeAsync(
        NpgsqlDataSource dataSource,
        string connectionString,
        GameplayContentPublicationResult authority)
    {
        await AssertChampionVectorAsync(
            dataSource,
            authority.Revision,
            inflated: false,
            mutable: false);
        await AssertChampionVectorAsync(
            dataSource,
            revision: null,
            inflated: false,
            mutable: true);

        await InflateMutableChampionTalentsAsync(dataSource);
        await ResetPublicationToDirectV3Async(dataSource);
        await AddMutableMarkerAndPoisonAsync(dataSource);
        WorldContentUnavailableException? rejection = null;
        try
        {
            _ = await PostgresGameplayContentPublisher
                .EnsurePublishedAsync(connectionString);
        }
        catch (WorldContentUnavailableException error)
        {
            rejection = error;
        }
        Check.True(
            rejection?.Reason == WorldContentFailureReason.RevisionMismatch,
            "mixed mutable Champion authority fails closed");
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            Check.Equal(
                InflatedV3Revision,
                await ReadCurrentRevisionAsync(connection),
                "mutable poison leaves the direct-v3 pointer unchanged");
        }

        await InflateMutableChampionTalentsAsync(dataSource);
        var direct = await PostgresGameplayContentPublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(
            direct.Created &&
            direct.Publisher == AuthorityPublisher &&
            direct.Revision == authority.Revision,
            "publisher-v1 direct v3 reuses the sealed corrected successor");
        await AssertChampionVectorAsync(
            dataSource,
            revision: null,
            inflated: false,
            mutable: true);
        await AssertMutableMarkerPreservedAsync(dataSource);
        await RemoveMutableMarkerAsync(dataSource);

        var repeated = await PostgresGameplayContentPublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(
            !repeated.Created && repeated.Revision == authority.Revision,
            "direct-v3 successor is idempotent after pointer advancement");
    }

    private static async Task ResetPublicationToDirectV3Async(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE gameplay_content_publication
            SET revision = @revision,
                publisher = @publisher,
                published_at = now()
            WHERE family = 'gameplay';
            """,
            connection);
        command.Parameters.AddWithValue("revision", InflatedV3Revision);
        command.Parameters.AddWithValue("publisher", LegacyPublisher);
        Check.Equal(1, await command.ExecuteNonQueryAsync(),
            "the integration fixture models a direct publisher-v1 v3 pointer");
    }

    private static async Task AddMutableMarkerAndPoisonAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE talent_templates
            SET stats = jsonb_set(stats, '{AuthorityTestMarker}', 'true'::jsonb)
            WHERE id = 50 AND class_id = 1;

            UPDATE talent_templates
            SET effect_value = 0.014,
                stats = jsonb_set(stats, ARRAY[effect_type], '"11,0.014"'::jsonb)
            WHERE id = 55 AND class_id = 1;
            """,
            connection);
        Check.Equal(2, await command.ExecuteNonQueryAsync(),
            "the integration fixture adds unrelated JSON and scalar poison");
    }

    private static async Task AssertMutableMarkerPreservedAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT stats ->> 'AuthorityTestMarker'
            FROM talent_templates
            WHERE id = 50 AND class_id = 1;
            """,
            connection);
        Check.Equal(
            "true",
            Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty,
            "narrow mutable repair preserves unrelated authoring JSON");
    }

    private static async Task RemoveMutableMarkerAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE talent_templates
            SET stats = stats - 'AuthorityTestMarker'
            WHERE id = 50 AND class_id = 1
              AND stats ? 'AuthorityTestMarker';
            """,
            connection);
        Check.Equal(1, await command.ExecuteNonQueryAsync(),
            "the integration fixture removes its mutable JSON marker");
    }

    private static async Task<int> CountGameplayRevisionsAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM gameplay_content_revisions;",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static void AssertChampionCatalog(
        IReadOnlyList<GameplayTalentDefinition> talents,
        bool inflated)
    {
        var champion = talents
            .Where(static talent => talent.ClassId == 1)
            .ToDictionary(static talent => talent.Id);
        Check.Equal(ChampionStock.Count, champion.Count,
            "the published Champion catalog contains exactly 19 talents");
        foreach (var expected in ChampionStock)
        {
            Check.True(champion.TryGetValue(expected.Key, out var talent),
                $"published Champion talent {expected.Key} is present");
            var value = inflated ? expected.Value * 2.6m : expected.Value;
            Check.Equal(value, talent!.EffectValue,
                $"published Champion talent {expected.Key} has its scalar");
            Check.True(
                talent.StatsJson.Contains(
                    $"\"{talent.EffectId},{Format(value)}\"",
                    StringComparison.Ordinal),
                $"published Champion talent {expected.Key} has matching stats");
        }
    }

    private static async Task AssertChampionVectorAsync(
        NpgsqlDataSource dataSource,
        string? revision,
        bool inflated,
        bool mutable)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var sql = mutable
            ? """
              SELECT id, effect_id, effect_type, effect_value,
                     stats ->> effect_type
              FROM talent_templates
              WHERE class_id = 1
              ORDER BY id;
              """
            : """
              SELECT id, effect_id, effect_type, effect_value,
                     stats ->> effect_type
              FROM gameplay_talent_definitions
              WHERE revision = @revision AND class_id = 1
              ORDER BY id;
              """;
        await using var command = new NpgsqlCommand(sql, connection);
        if (!mutable)
        {
            command.Parameters.AddWithValue("revision", revision!);
        }
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            Check.True(ChampionStock.TryGetValue(id, out var stock),
                $"Champion vector contains only reviewed talent ID {id}");
            var expected = inflated ? stock * 2.6m : stock;
            Check.Equal(expected, reader.GetDecimal(3),
                $"Champion talent {id} has its reviewed scalar");
            Check.Equal(
                $"{reader.GetInt16(1)},{Format(expected)}",
                reader.GetString(4),
                $"Champion talent {id} has matching raw stats");
            count++;
        }
        Check.Equal(ChampionStock.Count, count,
            "the Champion vector contains exactly 19 talents");
    }

    private static string Format(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);
}
