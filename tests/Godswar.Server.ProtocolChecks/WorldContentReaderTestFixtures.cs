using Godswar.Server.Application.World;

namespace Godswar.Server.ProtocolChecks;

internal static class WorldContentReaderTestFixtures
{
    private static readonly IWorldContentReader EmptyReader =
        PinnedWorldContentReader.Create(
            "test-empty-v1",
            Enumerable.Range(0, byte.MaxValue + 1)
                .Select(static mapId => checked((short)mapId)),
            [],
            [],
            [],
            new DateTimeOffset(
                2026,
                7,
                29,
                0,
                0,
                0,
                TimeSpan.Zero),
            gameplay: GameplayContentTestFixtures.Published);

    public static IWorldContentReader Empty => EmptyReader;

    public static IWorldContentReader Create(
        IEnumerable<short> mapIds,
        IEnumerable<NpcSpawnDefinition>? npcs = null,
        IEnumerable<CapturedMonsterSpawn>? monsters = null,
        IEnumerable<byte[]>? enterBootstrapPackets = null) =>
        PinnedWorldContentReader.Create(
            "test-published-v1",
            mapIds,
            npcs ?? [],
            monsters ?? [],
            enterBootstrapPackets ?? [],
            new DateTimeOffset(
                2026,
                7,
                29,
                0,
                0,
                0,
                TimeSpan.Zero),
            gameplay: GameplayContentTestFixtures.Published);
}
