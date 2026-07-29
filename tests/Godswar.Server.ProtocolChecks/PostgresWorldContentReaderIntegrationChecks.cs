using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Verifies the pinned world-content projection and official NPC release on
/// the disposable B03 empty database.
/// </summary>
internal static partial class PostgresWorldContentReaderIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const short FixtureMapId = 0;
    private const uint FixtureMonsterObjectId = 0x7FFF0001;
    private const long HistoricalPacketTransactionId = -90505005;
    private const string FixtureSource = "b05_protocol_check";
    private const string InitialDisplayName = "B05 pinned fixture";
    private const string MutatedDisplayName = "B05 backing mutation";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pinned world-content baseline " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }
        _ = await PostgresNpcContentBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var captureSessionId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var fixture = CreateMonsterFixture();
        var historicalPacket = CreateHistoricalBootstrapDecoy();
        var fixturesInserted = false;

        try
        {
            await InsertFixturesAsync(
                dataSource,
                fixture,
                historicalPacket,
                captureSessionId,
                connectionId);
            fixturesInserted = true;

            var generatedMaps =
                await GeneratedWorldContentReaderLoader.LoadAsync();
            var first =
                await PostgresWorldContentReaderLoader.LoadAsync(
                    connectionString);
            var second =
                await PostgresWorldContentReaderLoader.LoadAsync(
                    connectionString);

            AssertStableManifest(first.Manifest, second.Manifest);
            AssertGeneratedMapFamily(
                generatedMaps.Manifest,
                first.Manifest);
            await AssertOfficialNpcReleaseAsync(dataSource, first);
            await AssertPublishedCatalogShapeAsync(dataSource, first);
            await AssertMapAndNpcProjectionAsync(
                first,
                second);
            await AssertPublishedBootstrapOnlyAsync(
                dataSource,
                first,
                historicalPacket);
            await AssertMonsterFixtureAsync(first, fixture, InitialDisplayName);
            await AssertMonsterFixtureAsync(second, fixture, InitialDisplayName);

            var pinnedRevision = first.Manifest.Revision;
            var pinnedMonsterRevision = first.Manifest.Monsters.Sha256;
            await MutateFixtureDisplayNameAsync(dataSource);

            await AssertMonsterFixtureAsync(first, fixture, InitialDisplayName);
            Check.Equal(
                pinnedRevision,
                first.Manifest.Revision,
                "loaded world catalog keeps its pinned manifest revision");
            Check.Equal(
                pinnedMonsterRevision,
                first.Manifest.Monsters.Sha256,
                "loaded world catalog keeps its pinned monster revision");

            var refreshed =
                await PostgresWorldContentReaderLoader.LoadAsync(
                    connectionString);
            Check.True(
                !string.Equals(
                    pinnedRevision,
                    refreshed.Manifest.Revision,
                    StringComparison.Ordinal),
                "a new catalog observes the backing-row revision change");
            Check.True(
                !string.Equals(
                    pinnedMonsterRevision,
                    refreshed.Manifest.Monsters.Sha256,
                    StringComparison.Ordinal),
                "a new catalog observes the monster-family revision change");
            Check.Equal(
                first.Manifest.Maps.Sha256,
                refreshed.Manifest.Maps.Sha256,
                "monster mutation does not change the map revision");
            Check.Equal(
                first.Manifest.Npcs.Sha256,
                refreshed.Manifest.Npcs.Sha256,
                "monster mutation does not change the NPC revision");
            await AssertMonsterFixtureAsync(
                refreshed,
                fixture,
                MutatedDisplayName);
        }
        finally
        {
            if (fixturesInserted)
            {
                await DeleteExactFixturesAsync(
                    dataSource,
                    captureSessionId);
            }
        }
    }

    private static void AssertStableManifest(
        WorldContentManifest first,
        WorldContentManifest second)
    {
        Check.Equal(
            first.Source,
            second.Source,
            "two PostgreSQL loads use the same source");
        Check.Equal(
            first.Revision,
            second.Revision,
            "two unchanged PostgreSQL loads are revision deterministic");
        AssertSameFamily(first.Maps, second.Maps, "map");
        AssertSameFamily(first.Npcs, second.Npcs, "NPC");
        AssertSameFamily(first.Monsters, second.Monsters, "monster");
        AssertSameFamily(
            first.EnterBootstrap,
            second.EnterBootstrap,
            "enter-bootstrap");
    }

    private static void AssertGeneratedMapFamily(
        WorldContentManifest generated,
        WorldContentManifest postgres)
    {
        AssertSameFamily(
            generated.Maps,
            postgres.Maps,
            "generated/PostgreSQL map");
    }

    private static void AssertSameFamily(
        WorldContentFamilyRevision expected,
        WorldContentFamilyRevision actual,
        string scope)
    {
        Check.Equal(
            expected.Family,
            actual.Family,
            $"{scope} family name");
        Check.Equal(
            expected.EntryCount,
            actual.EntryCount,
            $"{scope} published count");
        Check.Equal(
            expected.Sha256,
            actual.Sha256,
            $"{scope} canonical checksum");
    }

    private static async Task AssertPublishedCatalogShapeAsync(
        NpgsqlDataSource dataSource,
        IWorldContentReader postgres)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM map_templates),
                (SELECT COUNT(*) FROM npc_spawn_packets),
                (SELECT COUNT(*) FROM monster_spawn_packets);
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "published-catalog shape query returns one row");
        Check.Equal(
            checked((int)reader.GetInt64(0)),
            postgres.Manifest.Maps.EntryCount,
            "PostgreSQL map rows match the pinned map count");
        Check.Equal(
            0L,
            reader.GetInt64(1),
            "disposable source-parity baseline has no captured NPC override");
        Check.Equal(
            1L,
            reader.GetInt64(2),
            "disposable baseline has only the tracked monster fixture");
    }

    private static async Task AssertOfficialNpcReleaseAsync(
        NpgsqlDataSource dataSource,
        IWorldContentReader postgres)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision,
                   release.entry_count,
                   release.source,
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_spawn_definitions definitions
                       WHERE definitions.revision = publication.revision
                   )
            FROM npc_content_publication publication
            JOIN npc_content_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'npcs';
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "official NPC release metadata exists");
        Check.Equal(
            NpcContentBaselineV1.ExpectedRevision,
            reader.GetString(0),
            "official NPC publication revision");
        Check.Equal(
            NpcContentBaselineV1.ExpectedEntryCount,
            reader.GetInt32(1),
            "official NPC release entry count");
        Check.Equal(
            NpcContentBaselineV1.Source,
            reader.GetString(2),
            "official NPC release source");
        Check.Equal(
            NpcContentBaselineV1.ExpectedEntryCount,
            reader.GetInt32(3),
            "official NPC release stored definition count");
        Check.True(
            !await reader.ReadAsync(),
            "official NPC publication metadata is singular");
        Check.Equal(
            NpcContentBaselineV1.ExpectedRevision,
            postgres.Manifest.Npcs.Sha256,
            "pinned reader uses the official NPC release revision");
        Check.Equal(
            NpcContentBaselineV1.ExpectedEntryCount,
            postgres.Manifest.Npcs.EntryCount,
            "pinned reader uses every official NPC definition");
    }

    private static async Task AssertMapAndNpcProjectionAsync(
        IWorldContentReader first,
        IWorldContentReader second)
    {
        var mapIds = MapTemplateSeeds.Maps
            .Select(static map => map.MapId)
            .Distinct()
            .Order()
            .ToArray();
        Check.Equal(
            mapIds.Length,
            first.Manifest.Maps.EntryCount,
            "source map count matches the pinned PostgreSQL count");

        foreach (var mapId in mapIds)
        {
            var firstMap = await first.ReadMapAsync(mapId);
            var secondMap = await second.ReadMapAsync(mapId);
            AssertNpcSequence(
                firstMap.Npcs,
                secondMap.Npcs,
                $"map {mapId} repeat-load NPC");
        }
    }

    private static void AssertNpcSequence(
        IReadOnlyList<NpcSpawnDefinition> expected,
        IReadOnlyList<NpcSpawnDefinition> actual,
        string scope)
    {
        Check.Equal(expected.Count, actual.Count, $"{scope} count");
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = actual[index];
            Check.True(
                left.MapId == right.MapId &&
                string.Equals(
                    left.SceneKey,
                    right.SceneKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    left.NpcKey,
                    right.NpcKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    left.TemplateKey,
                    right.TemplateKey,
                    StringComparison.Ordinal) &&
                left.ObjectId == right.ObjectId &&
                left.X.Equals(right.X) &&
                left.Z.Equals(right.Z) &&
                left.InteractionId == right.InteractionId &&
                left.AppearanceType == right.AppearanceType &&
                left.Facing.Equals(right.Facing) &&
                left.Detail10077.SequenceEqual(right.Detail10077) &&
                left.Detail10080.SequenceEqual(right.Detail10080),
                $"{scope} row {index} is byte-identical");
        }
    }

    private static async Task AssertPublishedBootstrapOnlyAsync(
        NpgsqlDataSource dataSource,
        IWorldContentReader reader,
        byte[] historicalPacket)
    {
        var bootstrap = await reader.ReadEnterBootstrapAsync();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT clear_bytes
            FROM server_packet_templates
            WHERE template_key = 'enter_syn_game_data'
              AND direction = 'S2C'
              AND opcode = 10090
            ORDER BY sequence;
            """,
            connection);
        var published = new List<byte[]>();
        await using var result = await command.ExecuteReaderAsync();
        while (await result.ReadAsync())
        {
            published.Add((byte[])result["clear_bytes"]);
        }

        Check.Equal(
            published.Count,
            bootstrap.Packets.Count,
            "bootstrap contains only explicitly published templates");
        Check.Equal(
            published.Count,
            bootstrap.Revision.EntryCount,
            "bootstrap revision count matches published templates");
        for (var index = 0; index < published.Count; index++)
        {
            Check.True(
                published[index].SequenceEqual(bootstrap.Packets[index]),
                $"published bootstrap packet {index} is byte-identical");
        }

        Check.True(
            !bootstrap.Packets.Any(packet =>
                packet.SequenceEqual(historicalPacket)),
            "packet_transactions history is never a bootstrap fallback");
    }

    private static async Task AssertMonsterFixtureAsync(
        IWorldContentReader reader,
        CapturedMonsterSpawn fixture,
        string expectedDisplayName)
    {
        var map = await reader.ReadMapAsync(fixture.MapId);
        var actual = map.Monsters.Single(monster =>
            monster.ObjectId == fixture.ObjectId);
        Check.Equal(
            expectedDisplayName,
            actual.DisplayName,
            "tracked monster display name");
        Check.True(
            actual.MapId == fixture.MapId &&
            string.Equals(
                actual.SceneKey,
                fixture.SceneKey,
                StringComparison.Ordinal) &&
            string.Equals(
                actual.TemplateKey,
                fixture.TemplateKey,
                StringComparison.Ordinal) &&
            actual.ObjectId == fixture.ObjectId &&
            actual.X.Equals(fixture.X) &&
            actual.Z.Equals(fixture.Z) &&
            actual.Packet.SequenceEqual(fixture.Packet),
            "tracked captured monster reads byte-identically");
    }

}
