using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Proves the one-time, reviewed NPC baseline publication and the immutable
/// PostgreSQL runtime authority used by B05B.
/// </summary>
internal static partial class
    PostgresNpcContentPublicationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string ExpectedRevision =
        "06BCC3DD4665BB5F3F3AE0843B1AA2A1B6C211DDA07DB0381B5EA663068040C7";
    private const string ExpectedSource =
        "reviewed-legacy-projection-v1";
    private const int ExpectedEntryCount = 383;
    private const string LegacySource = "b05b_legacy_decoy";
    private const short LegacyMapId = 0;
    private const int LegacyQuestId = -90505006;
    private const string LegacyNpcKey =
        "b05b_legacy_decoy_npc";
    private const string LegacyTemplateKey =
        "b05b_legacy_decoy_template";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL official NPC content publication " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await AssertUnpublishedDatabaseAsync(dataSource);
        await AssertLoaderFailsClosedAsync(connectionString);

        var coldRace = await Task.WhenAll(
            Enumerable.Range(0, 6)
                .Select(_ =>
                    PostgresNpcContentBaselinePublisher
                        .EnsurePublishedAsync(connectionString)));
        Check.Equal(
            1,
            coldRace.Count(static result => result.Created),
            "one cold-race publisher creates the official NPC release");
        foreach (var result in coldRace)
        {
            AssertPublicationResult(
                result,
                created: result.Created);
        }

        // The production reader is intentionally all-or-nothing across its
        // published content families. Supply the independently tested
        // dialogue family before using that reader to verify NPC isolation.
        _ = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        var pinned =
            await PostgresWorldContentReaderLoader.LoadAsync(
                connectionString);
        AssertOfficialNpcManifest(pinned.Manifest);
        var databaseDefinitions =
            await ReadPublishedDefinitionsAsync(dataSource);
        var reviewedDefinitions =
            NpcContentBaselineV1.LoadDefinitions();
        AssertNpcSequence(
            reviewedDefinitions,
            databaseDefinitions,
            "reviewed artifact/PostgreSQL round trip");
        var pinnedDefinitions =
            await ReadAllNpcDefinitionsAsync(
                dataSource,
                pinned);
        AssertNpcSequence(
            databaseDefinitions,
            pinnedDefinitions,
            "PostgreSQL/reader round trip");

        var repeat =
            await PostgresNpcContentBaselinePublisher
                .EnsurePublishedAsync(connectionString);
        AssertPublicationResult(repeat, created: false);

        await AssertSingletonPublicationAsync(dataSource);

        var legacyFixturesInserted = false;
        try
        {
            await InsertLegacyNpcSourceFixturesAsync(dataSource);
            legacyFixturesInserted = true;
            var refreshed =
                await PostgresWorldContentReaderLoader.LoadAsync(
                    connectionString);
            AssertOfficialNpcManifest(refreshed.Manifest);
            Check.Equal(
                pinned.Manifest.Npcs.Sha256,
                refreshed.Manifest.Npcs.Sha256,
                "legacy-source mutation cannot change the NPC revision");
            var refreshedDefinitions =
                await ReadAllNpcDefinitionsAsync(
                    dataSource,
                    refreshed);
            AssertNpcSequence(
                pinnedDefinitions,
                refreshedDefinitions,
                "legacy-source mutation cannot change loaded NPCs");
        }
        finally
        {
            if (legacyFixturesInserted)
            {
                await DeleteLegacyNpcSourceFixturesAsync(dataSource);
            }
        }

        await AssertImmutableDatabaseGuardsAsync(dataSource);
        AssertOfficialNpcManifest(pinned.Manifest);
        var finalPinnedDefinitions =
            await ReadAllNpcDefinitionsAsync(dataSource, pinned);
        AssertNpcSequence(
            pinnedDefinitions,
            finalPinnedDefinitions,
            "process-pinned reader remains unchanged");
        await AssertSingletonPublicationAsync(dataSource);
    }

    private static async Task AssertLoaderFailsClosedAsync(
        string connectionString)
    {
        try
        {
            _ = await PostgresWorldContentReaderLoader.LoadAsync(
                connectionString);
        }
        catch (WorldContentUnavailableException ex)
        {
            Check.Equal(
                "npcs",
                ex.Family,
                "missing publication failure family");
            Check.True(
                ex.Reason == WorldContentFailureReason.Missing,
                "missing publication failure reason is typed Missing");
            return;
        }

        throw new InvalidOperationException(
            "The PostgreSQL world loader accepted an unpublished NPC family.");
    }

    private static void AssertPublicationResult(
        NpcContentPublicationResult result,
        bool created)
    {
        Check.Equal(
            ExpectedRevision,
            result.Revision,
            "published NPC baseline revision");
        Check.Equal(
            ExpectedEntryCount,
            result.EntryCount,
            "published NPC baseline entry count");
        Check.Equal(
            ExpectedSource,
            result.Source,
            "published NPC baseline source");
        Check.Equal(
            created,
            result.Created,
            "published NPC baseline creation outcome");
    }

    private static void AssertOfficialNpcManifest(
        WorldContentManifest manifest)
    {
        Check.Equal(
            "npcs",
            manifest.Npcs.Family,
            "official NPC manifest family");
        Check.Equal(
            ExpectedRevision,
            manifest.Npcs.Sha256,
            "official NPC manifest revision");
        Check.Equal(
            ExpectedEntryCount,
            manifest.Npcs.EntryCount,
            "official NPC manifest entry count");
    }
}
