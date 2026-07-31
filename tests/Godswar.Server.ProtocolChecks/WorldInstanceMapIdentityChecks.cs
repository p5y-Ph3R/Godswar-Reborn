using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static class WorldInstanceMapIdentityChecks
{
    public const string CheckName =
        "B18A live map-instance identity bridge";

    public static Task RunAsync()
    {
        var legacy = new MapInstance(13);
        Check.Equal(
            RealmId.Tempest,
            legacy.RealmId,
            "legacy map runtime belongs to Tempest");
        Check.Equal(
            (byte)13,
            legacy.MapId,
            "legacy portal map ID remains byte-compatible");
        Check.Equal(
            (short)13,
            legacy.ContentMapId.Value,
            "typed content map identity preserves the legacy value");
        Check.True(
            legacy.InstanceKind == InstanceKind.OpenWorld,
            "legacy map runtime remains an open-world instance");
        Check.True(
            legacy.WorldInstanceId.IsValid,
            "legacy map runtime receives an opaque world-instance identity");

        var sharedMap = new WorldMapId(40);
        var firstDungeon = new MapInstance(
            CreateDescriptor(
                sharedMap,
                InstanceKind.Dungeon));
        var secondDungeon = new MapInstance(
            CreateDescriptor(
                sharedMap,
                InstanceKind.Dungeon));
        Check.True(
            firstDungeon.WorldInstanceId !=
                secondDungeon.WorldInstanceId,
            "two dungeon runtimes sharing content retain distinct identities");
        Check.Equal(
            firstDungeon.ContentMapId,
            secondDungeon.ContentMapId,
            "world-instance identity is independent from map definition");

        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new MapInstance(
                CreateDescriptor(
                    new WorldMapId(256),
                    InstanceKind.Battlefield)),
            "extended map IDs cannot silently enter the legacy byte runtime");

        return Task.CompletedTask;
    }

    private static WorldInstanceDescriptor CreateDescriptor(
        WorldMapId mapId,
        InstanceKind kind) =>
        WorldInstanceDescriptor.Create(
            RealmId.Tempest,
            WorldInstanceId.New(),
            mapId,
            kind,
            playerCapacity: 100,
            DateTimeOffset.UtcNow);
}
