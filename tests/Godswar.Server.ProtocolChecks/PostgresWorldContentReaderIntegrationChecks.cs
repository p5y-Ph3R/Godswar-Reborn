using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Verifies pinned world content and official immutable publications on the
/// disposable B03 empty database.
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

        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        await AssertPartialRelationalBaselineRejectedAsync(
            connectionString);
        await AssertPoisonedGameplayReleaseRejectedAsync(
            connectionString);
        _ = await PostgresNpcContentBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresMonsterContentBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresEnterBootstrapBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresGameplayContentPublisher
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
            await AssertPublishedGameplayPermitsMissingSourceAsync(
                dataSource);
            await AssertPublishedGameplayIgnoresSourceMutationAsync(
                dataSource,
                connectionString,
                first);
            AssertGeneratedMapFamily(
                generatedMaps.Manifest,
                first.Manifest);
            await AssertOfficialNpcReleaseAsync(dataSource, first);
            await AssertOfficialMonsterReleaseAsync(dataSource, first);
            await AssertPublishedCatalogShapeAsync(dataSource, first);
            await AssertMapAndNpcProjectionAsync(
                first,
                second);
            await AssertPublishedBootstrapOnlyAsync(
                dataSource,
                first,
                historicalPacket);
            await AssertMonsterFixtureIsResearchOnlyAsync(first, fixture);
            await AssertMonsterFixtureIsResearchOnlyAsync(second, fixture);

            var pinnedRevision = first.Manifest.Revision;
            var pinnedMonsterRevision = first.Manifest.Monsters.Sha256;
            await MutateFixtureDisplayNameAsync(dataSource);

            await AssertMonsterFixtureIsResearchOnlyAsync(first, fixture);
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
            Check.Equal(
                pinnedRevision,
                refreshed.Manifest.Revision,
                "capture-table mutation cannot change a new official catalog");
            Check.Equal(
                pinnedMonsterRevision,
                refreshed.Manifest.Monsters.Sha256,
                "capture-table mutation cannot change the official monster family");
            Check.Equal(
                first.Manifest.Maps.Sha256,
                refreshed.Manifest.Maps.Sha256,
                "monster mutation does not change the map revision");
            Check.Equal(
                first.Manifest.Npcs.Sha256,
                refreshed.Manifest.Npcs.Sha256,
                "monster mutation does not change the NPC revision");
            await AssertMonsterFixtureIsResearchOnlyAsync(
                refreshed,
                fixture);
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
        AssertSameFamily(first.Gameplay, second.Gameplay, "gameplay");
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
        Check.Equal(
            0,
            generated.Gameplay.EntryCount,
            "archived generated fixture does not masquerade as gameplay authority");
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

    private static async Task AssertOfficialMonsterReleaseAsync(
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
                       FROM monster_spawn_definitions definitions
                       WHERE definitions.revision = publication.revision
                   )
            FROM monster_content_publication publication
            JOIN monster_content_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'monsters';
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "official monster release metadata exists");
        Check.Equal(
            MonsterContentBaselineV1.ExpectedRevision,
            reader.GetString(0),
            "official monster publication revision");
        Check.Equal(
            MonsterContentBaselineV1.ExpectedEntryCount,
            reader.GetInt32(1),
            "official monster release entry count");
        Check.Equal(
            MonsterContentBaselineV1.Source,
            reader.GetString(2),
            "official monster release source");
        Check.Equal(
            MonsterContentBaselineV1.ExpectedEntryCount,
            reader.GetInt32(3),
            "official monster stored definition count");
        Check.True(
            !await reader.ReadAsync(),
            "official monster publication metadata is singular");
        Check.Equal(
            MonsterContentBaselineV1.ExpectedRevision,
            postgres.Manifest.Monsters.Sha256,
            "pinned reader uses the official monster release revision");
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
            SELECT publication.revision,
                   release.packet_count,
                   release.total_bytes,
                   release.source
            FROM enter_bootstrap_publication publication
            JOIN enter_bootstrap_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'enter-bootstrap';
            """,
            connection);
        await using var result = await command.ExecuteReaderAsync();
        Check.True(
            await result.ReadAsync(),
            "official enter-bootstrap release metadata exists");
        Check.Equal(
            result.GetString(0),
            bootstrap.Revision.Sha256,
            "bootstrap uses the published revision");
        Check.Equal(
            0,
            result.GetInt32(1),
            "safe baseline explicitly publishes zero packets");
        Check.Equal(
            0,
            result.GetInt32(2),
            "safe baseline explicitly publishes zero bytes");
        Check.Equal(
            "explicit-safe-empty-v1",
            result.GetString(3),
            "safe baseline source is explicit");
        Check.Equal(
            0,
            bootstrap.Packets.Count,
            "bootstrap contains no character-specific capture packet");
        Check.Equal(
            0,
            bootstrap.Revision.EntryCount,
            "bootstrap revision count matches the empty publication");

        Check.True(
            !bootstrap.Packets.Any(packet =>
                packet.SequenceEqual(historicalPacket)),
            "packet_transactions history is never a bootstrap fallback");
    }

    private static async Task AssertMonsterFixtureIsResearchOnlyAsync(
        IWorldContentReader reader,
        CapturedMonsterSpawn fixture)
    {
        var map = await reader.ReadMapAsync(fixture.MapId);
        Check.True(
            map.Monsters.All(monster =>
                monster.ObjectId != fixture.ObjectId),
            "capture-table monster remains research-only");
    }

}
