using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Godswar.Server.Application.World;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderChecks
{
    private static readonly DateTimeOffset FixedLoadTime =
        new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await CheckDeterministicRevisionAndOrderingAsync();
        await CheckPinnedDefensiveCopiesAsync();
        await CheckKnownEmptyAndUnknownMapsAsync();
        CheckMalformedContentRejections();
        CheckMonsterSpawnGameplayCompatibility();
        CheckExpectedRevisionGuard();
        CheckGameplayHashDomainSeparation();
        CheckMonsterCombatAuthority();
        CheckPvpWorldAuthority();
        await CheckSafeBootstrapParityAsync();
        await CheckMetricsAsync();
        await WorldContentReaderDialogueChecks.RunAsync();
    }

    private static async Task CheckDeterministicRevisionAndOrderingAsync()
    {
        var firstNpc = CreateNpc(
            mapId: 1,
            objectId: 0x9001,
            npcKey: "Athens_Artisan");
        var secondNpc = CreateNpc(
            mapId: 1,
            objectId: 0x9002,
            npcKey: "Athens_Vendor");
        var firstMonster = CreateTierFourMonster(
            objectId: 10069,
            mapId: 1);
        var secondMonster = CreateTierFourMonster(
            objectId: 10070,
            mapId: 1);
        var bootstrap = CreateSafeBootstrapPacket(0x11);

        var first = PinnedWorldContentReader.Create(
            "test-published-v1",
            [2, 1, 1],
            [secondNpc, firstNpc],
            [secondMonster, firstMonster],
            [bootstrap],
            FixedLoadTime);
        var second = PinnedWorldContentReader.Create(
            "different-runtime-label",
            [1, 2],
            [firstNpc, secondNpc],
            [firstMonster, secondMonster],
            [bootstrap.ToArray()],
            FixedLoadTime.AddDays(1));

        Check.Equal(
            first.Manifest.Revision,
            second.Manifest.Revision,
            "manifest revision ignores caller enumeration order and runtime metadata");
        Check.Equal(
            first.Manifest.Maps.Sha256,
            second.Manifest.Maps.Sha256,
            "map-family revision is deterministic");
        Check.Equal(
            first.Manifest.Npcs.Sha256,
            second.Manifest.Npcs.Sha256,
            "NPC-family revision is deterministic");
        Check.Equal(
            first.Manifest.Monsters.Sha256,
            second.Manifest.Monsters.Sha256,
            "monster-family revision is deterministic");
        Check.Equal(
            first.Manifest.EnterBootstrap.Sha256,
            second.Manifest.EnterBootstrap.Sha256,
            "bootstrap-family revision is deterministic");
        Check.Equal(
            "B837DC4436EB9E2B88E0336DDC3CF7DAA6A18D26B5C5A412A3E102600A04D8A2",
            first.Manifest.Maps.Sha256,
            "map-family canonical revision golden vector");
        Check.Equal(
            "D582471A6840116D7955D3C198FAC8434A663261303AB4EB5A71960CE838534C",
            first.Manifest.Npcs.Sha256,
            "NPC-family canonical revision golden vector");
        Check.Equal(
            "63A0441378107204ED761587C8CB057056E1DE02A8A487BEB6A730AC52843804",
            first.Manifest.Monsters.Sha256,
            "monster-family canonical revision golden vector");
        Check.Equal(
            "5967B4DCAA178E4F107175AD8BD01E77B05C40344D3CECA117781D39AE664938",
            first.Manifest.EnterBootstrap.Sha256,
            "bootstrap-family canonical revision golden vector");
        Check.Equal(
            "BA19F5AB76820A9939385700F14E88EC9582ADEEA5D92449C922EC887542E852",
            first.Manifest.Revision,
            "combined content-manifest canonical revision golden vector");
        Check.Equal(2, first.Manifest.Maps.EntryCount, "duplicate map IDs are canonicalized");
        Check.Equal(2, first.Manifest.Npcs.EntryCount, "NPC manifest entry count");
        Check.Equal(
            0,
            first.Manifest.NpcDialogues.EntryCount,
            "optional NPC dialogue manifest is empty");
        Check.Equal(2, first.Manifest.Monsters.EntryCount, "monster manifest entry count");
        Check.Equal(1, first.Manifest.EnterBootstrap.EntryCount, "bootstrap manifest entry count");

        var map = await first.ReadMapAsync(1);
        Check.True(
            map.Npcs.Select(static value => value.ObjectId)
                .SequenceEqual([0x9001u, 0x9002u]),
            "NPC content is pinned in canonical order");
        Check.True(
            map.Monsters.Select(static value => value.ObjectId)
                .SequenceEqual([10069u, 10070u]),
            "monster content is pinned in canonical order");
        Check.Equal(
            first.Manifest.Npcs.Sha256,
            map.NpcRevision.Sha256,
            "map read reports the process-pinned NPC revision");
        Check.Equal(
            first.Manifest.Monsters.Sha256,
            map.MonsterRevision.Sha256,
            "map read reports the process-pinned monster revision");
    }

    private static async Task CheckPinnedDefensiveCopiesAsync()
    {
        var npcDetail10077 = new byte[] { 1, 2, 3 };
        var npcDetail10080 = new byte[] { 4, 5, 6 };
        var npc = CreateNpc(
            mapId: 1,
            objectId: 0x9010,
            npcKey: "Athens_Mentor",
            detail10077: npcDetail10077,
            detail10080: npcDetail10080);
        var monster = CreateTierFourMonster(10071, 1);
        var originalMonsterPacket = monster.Packet.ToArray();
        var bootstrap = CreateSafeBootstrapPacket(0x22);
        var originalBootstrap = bootstrap.ToArray();

        var reader = PinnedWorldContentReader.Create(
            "test-published-v1",
            [1],
            [npc],
            [monster],
            [bootstrap],
            FixedLoadTime);
        var pinnedRevision = reader.Manifest.Revision;

        npcDetail10077[0] = 0xFF;
        npcDetail10080[0] = 0xFF;
        monster.Packet[0] = 0xFF;
        bootstrap[0] = 0xFF;

        var firstRead = await reader.ReadMapAsync(1);
        var firstBootstrap = await reader.ReadEnterBootstrapAsync();
        Check.Equal((byte)1, firstRead.Npcs[0].Detail10077[0], "NPC detail 10077 input is cloned");
        Check.Equal((byte)4, firstRead.Npcs[0].Detail10080[0], "NPC detail 10080 input is cloned");
        Check.True(
            firstRead.Monsters[0].Packet.SequenceEqual(originalMonsterPacket),
            "monster packet input is cloned");
        Check.True(
            firstBootstrap.Packets[0].SequenceEqual(originalBootstrap),
            "bootstrap packet input is cloned");

        firstRead.Npcs[0].Detail10077[0] = 0xEE;
        firstRead.Monsters[0].Packet[0] = 0xEE;
        firstBootstrap.Packets[0][0] = 0xEE;

        var secondRead = await reader.ReadMapAsync(1);
        var secondBootstrap = await reader.ReadEnterBootstrapAsync();
        Check.Equal((byte)1, secondRead.Npcs[0].Detail10077[0], "NPC read result is cloned");
        Check.True(
            secondRead.Monsters[0].Packet.SequenceEqual(originalMonsterPacket),
            "monster read result is cloned");
        Check.True(
            secondBootstrap.Packets[0].SequenceEqual(originalBootstrap),
            "bootstrap read result is cloned");
        Check.Equal(
            pinnedRevision,
            reader.Manifest.Revision,
            "caller mutations cannot alter the pinned revision");
    }

    private static async Task CheckKnownEmptyAndUnknownMapsAsync()
    {
        var reader = PinnedWorldContentReader.Create(
            "test-published-v1",
            [1, 2],
            [CreateNpc(1, 0x9020, "Athens_Guide")],
            [CreateTierFourMonster(10072, 1)],
            [],
            FixedLoadTime);

        var knownEmpty = await reader.ReadMapAsync(2);
        Check.Equal(0, knownEmpty.Npcs.Count, "published empty map has no NPCs");
        Check.Equal(0, knownEmpty.Monsters.Count, "published empty map has no monsters");

        var exception = await CaptureUnavailableAsync(
            () => reader.ReadMapAsync(3));
        Check.Equal("maps", exception.Family, "unknown map failure family");
        Check.True(
            exception.Reason == WorldContentFailureReason.Missing,
            "unknown map has a typed missing-content failure");
    }

    private static void CheckMalformedContentRejections()
    {
        var noMaps = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "test-published-v1",
                [],
                [],
                [],
                [],
                FixedLoadTime));
        Check.Equal("maps", noMaps.Family, "empty publication failure family");
        Check.True(
            noMaps.Reason == WorldContentFailureReason.Missing,
            "empty publication is rejected as missing");

        var badNpc = CreateNpc(1, 0, "Athens_Invalid");
        var invalidNpc = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "test-published-v1",
                [1],
                [badNpc],
                [],
                [],
                FixedLoadTime));
        Check.Equal("npcs", invalidNpc.Family, "malformed NPC failure family");
        Check.True(
            invalidNpc.Reason == WorldContentFailureReason.Invalid,
            "malformed NPC is rejected as invalid");

        var malformedMonster = CreateTierFourMonster(10073, 1);
        malformedMonster.Packet[2] = 0;
        var invalidMonster = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "test-published-v1",
                [1],
                [],
                [malformedMonster],
                [],
                FixedLoadTime));
        Check.Equal(
            "monsters",
            invalidMonster.Family,
            "malformed monster failure family");
        Check.True(
            invalidMonster.Reason == WorldContentFailureReason.Invalid,
            "malformed monster is rejected as invalid");

        var invalidBootstrap = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "test-published-v1",
                [1],
                [],
                [],
                [new byte[] { 8, 0, 0, 0 }],
                FixedLoadTime));
        Check.Equal(
            "enter-bootstrap",
            invalidBootstrap.Family,
            "malformed bootstrap failure family");
        Check.True(
            invalidBootstrap.Reason == WorldContentFailureReason.Invalid,
            "malformed bootstrap is rejected as invalid");
    }

    private static void CheckExpectedRevisionGuard()
    {
        var reader = PinnedWorldContentReader.Create(
            "test-published-v1",
            [1],
            [],
            [],
            [],
            FixedLoadTime);
        reader.RequireRevision(reader.Manifest.Revision.ToLowerInvariant());

        var exception = CaptureUnavailable(() =>
            reader.RequireRevision(new string('0', 64)));
        Check.Equal("manifest", exception.Family, "revision mismatch failure family");
        Check.True(
            exception.Reason == WorldContentFailureReason.RevisionMismatch,
            "expected revision mismatch is typed");
    }

    private static async Task CheckSafeBootstrapParityAsync()
    {
        var firstPacket = CreateSafeBootstrapPacket(0x31);
        var secondPacket = CreateSafeBootstrapPacket(0x32);
        var reader = PinnedWorldContentReader.Create(
            "test-published-v1",
            [1],
            [],
            [],
            [firstPacket, secondPacket],
            FixedLoadTime);

        var content = await reader.ReadEnterBootstrapAsync();
        Check.Equal(2, content.Packets.Count, "all published bootstrap packets are returned");
        Check.True(
            content.Packets[0].SequenceEqual(firstPacket) &&
            content.Packets[1].SequenceEqual(secondPacket),
            "safe bootstrap bytes and publication order are preserved exactly");
        Check.Equal(
            reader.Manifest.EnterBootstrap.Sha256,
            content.Revision.Sha256,
            "bootstrap read reports its pinned family revision");
    }

    private static async Task CheckMetricsAsync()
    {
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, candidate) =>
        {
            if (instrument.Meter.Name == WorldContentMetrics.MeterName)
            {
                candidate.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
                measurements.Enqueue(new CapturedMeasurement(
                    instrument.Name,
                    measurement,
                    tags.ToArray())));
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
                measurements.Enqueue(new CapturedMeasurement(
                    instrument.Name,
                    measurement,
                    tags.ToArray())));
        listener.Start();

        WorldContentMetrics.RecordLoad(
            "test-published-v1",
            "success",
            TimeSpan.FromMilliseconds(3));
        WorldContentMetrics.RecordFallbackAttempt("legacy-test");
        var reader = PinnedWorldContentReader.Create(
            "test-published-v1",
            [1],
            [],
            [],
            [],
            FixedLoadTime);
        _ = await CaptureUnavailableAsync(() => reader.ReadMapAsync(2));
        _ = CaptureUnavailable(() =>
            reader.RequireRevision(new string('F', 64)));

        var captured = measurements.ToArray();
        Check.True(
            captured.Any(static value =>
                value.Name == "godswar_world_content_loads_total" &&
                HasTag(value, "source", "test-published-v1") &&
                HasTag(value, "outcome", "success")),
            "successful content load outcome is observable");
        Check.True(
            captured.Any(static value =>
                value.Name == "godswar_world_content_load_duration_ms" &&
                value.Value >= 3),
            "content load latency is observable");
        Check.True(
            captured.Any(static value =>
                value.Name == "godswar_world_content_rejections_total" &&
                HasTag(value, "family", "maps") &&
                HasTag(value, "reason", "missing")),
            "missing content rejection is observable");
        Check.True(
            captured.Any(static value =>
                value.Name == "godswar_world_content_rejections_total" &&
                HasTag(value, "family", "manifest") &&
                HasTag(value, "reason", "revision_mismatch")),
            "revision mismatch rejection is observable");
        Check.True(
            captured.Any(static value =>
                value.Name ==
                "godswar_world_content_fallback_attempts_total" &&
                HasTag(value, "source", "legacy-test")),
            "legacy fallback attempts are observable");
    }

    private static NpcSpawnDefinition CreateNpc(
        short mapId,
        uint objectId,
        string npcKey,
        byte[]? detail10077 = null,
        byte[]? detail10080 = null) =>
        new(
            mapId,
            mapId == 1 ? "Athens" : "Sparta",
            npcKey,
            $"{npcKey}_Male1",
            objectId,
            10.25f,
            -20.5f,
            objectId,
            0x00040002,
            1.25f,
            detail10077 ?? [0x77],
            detail10080 ?? [0x80]);

    private static CapturedMonsterSpawn CreateTierFourMonster(
        uint objectId,
        short mapId)
    {
        var packet = Convert.FromHexString(
            "6C00242712020000752700000400000000000000320100003201000017ED144300000000E0D55F42B70B05C0415F6E6F726D616C5F737475625F3030330000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        return new CapturedMonsterSpawn(
            mapId,
            mapId == 1 ? "Athens" : "Sparta",
            "A_normal_stub_003",
            "captured tier-four monster",
            objectId,
            System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                packet.AsSpan(28, 4)),
            System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                packet.AsSpan(36, 4)),
            packet);
    }

    private static byte[] CreateSafeBootstrapPacket(byte marker) =>
    [
        8,
        0,
        0x6A,
        0x27,
        marker,
        0,
        0,
        0
    ];

    private static WorldContentUnavailableException CaptureUnavailable(
        Action action)
    {
        try
        {
            action();
        }
        catch (WorldContentUnavailableException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(
            "Expected WorldContentUnavailableException.");
    }

    private static async Task<WorldContentUnavailableException>
        CaptureUnavailableAsync(Func<ValueTask<WorldMapContent>> action)
    {
        try
        {
            _ = await action();
        }
        catch (WorldContentUnavailableException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(
            "Expected WorldContentUnavailableException.");
    }

    private static bool HasTag(
        CapturedMeasurement measurement,
        string name,
        string value) =>
        measurement.Tags.Any(
            tag => tag.Key == name && Equals(tag.Value, value));

    private readonly record struct CapturedMeasurement(
        string Name,
        double Value,
        KeyValuePair<string, object?>[] Tags);
}
