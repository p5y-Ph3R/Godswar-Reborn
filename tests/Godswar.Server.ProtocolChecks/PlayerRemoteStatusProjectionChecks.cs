using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerRemoteStatusProjectionChecks
{
    private const uint LocalPlayerObjectId = 0x0000_1448;
    private const uint RemotePlayerObjectId = 0x7135_B24E;
    private const int CampOffset = 62;
    private const int PkModeOffset = 232;

    public static void Run()
    {
        CheckPacketVectors();
        CheckInspectBundleVector();
        CheckRemoteEgressCoverage();
    }

    private static void CheckPacketVectors()
    {
        var character = CreateCharacter(GameDefaults.SpartaCamp);
        var aggregate = ClientStatusAggregate.Empty with
        {
            MovementSpeedMultiplier = 1.31f,
            PhysicalDefense = 47,
            MagicDefense = 53,
            Hit = 59,
            Dodge = 61,
            CriticalAppend = 67,
            CriticalResistance = 71
        };
        var defaultStatus = PacketBuilder.PlayerStatusUpdate(
            character,
            RemotePlayerObjectId);
        var remoteDefault = PacketBuilder.RemotePlayerStatusUpdate(
            character,
            RemotePlayerObjectId,
            ClientStatusAggregate.Empty,
            pkMode: null);
        Check.True(
            remoteDefault.SequenceEqual(defaultStatus),
            "ordinary remote default remains byte-identical to the legacy builder");

        var effectOnly = PacketBuilder.PlayerStatusUpdate(
            character,
            RemotePlayerObjectId,
            aggregate);
        var remoteEffectOnly = PacketBuilder.RemotePlayerStatusUpdate(
            character,
            RemotePlayerObjectId,
            aggregate,
            pkMode: null);

        Check.True(
            remoteEffectOnly.SequenceEqual(effectOnly),
            "ordinary remote status remains byte-identical to the effect-only builder");
        AssertIdentityBytes(
            remoteEffectOnly,
            GameDefaults.SpartaCamp,
            "ordinary Sparta remote status");
        Check.Equal(
            (byte)5,
            remoteEffectOnly[PkModeOffset],
            "ordinary remote status preserves captured PK mode 5");

        character.Camp = GameDefaults.AthensCamp;
        var athensDefault = PacketBuilder.RemotePlayerStatusUpdate(
            character,
            RemotePlayerObjectId,
            aggregate,
            pkMode: null);
        AssertOnlyOffsetsDiffer(
            remoteEffectOnly,
            athensDefault,
            CampOffset);
        AssertIdentityBytes(
            athensDefault,
            GameDefaults.AthensCamp,
            "ordinary Athens remote status");

        var trainingDummy = PacketBuilder.RemotePlayerStatusUpdate(
            character,
            RemotePlayerObjectId,
            aggregate,
            pkMode: 1);
        AssertOnlyOffsetsDiffer(
            athensDefault,
            trainingDummy,
            PkModeOffset);
        Check.Equal(
            (byte)1,
            trainingDummy[PkModeOffset],
            "exact training dummy projects PK mode 1");

        var local = PacketBuilder.PlayerStatusUpdate(character, aggregate);
        Check.Equal(
            LocalPlayerObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(local.AsSpan(4, 4)),
            "local status retains the local-player object id");
        AssertIdentityBytes(
            local,
            GameDefaults.AthensCamp,
            "local Athens status");
        Check.Equal(
            (byte)5,
            local[PkModeOffset],
            "local status preserves captured PK mode 5");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.RemotePlayerStatusUpdate(
                character,
                LocalPlayerObjectId,
                aggregate,
                pkMode: 1),
            "remote status rejects the local-player object id");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.RemotePlayerStatusUpdate(
                character,
                RemotePlayerObjectId,
                aggregate,
                pkMode: 2),
            "remote status rejects an unproven PK-mode override");
        character.Camp = 2;
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusUpdate(character, aggregate),
            "status rejects a camp outside the proven 0/1 domain");
    }

    private static void CheckInspectBundleVector()
    {
        var character = CreateCharacter(GameDefaults.AthensCamp);
        var aggregate = ClientStatusAggregate.Empty with
        {
            MovementSpeedMultiplier = 1.17f,
            Hit = 23
        };
        var expectedStatus = PacketBuilder.RemotePlayerStatusUpdate(
            character,
            RemotePlayerObjectId,
            aggregate,
            pkMode: 1);
        var bundle = PacketBuilder.PlayerInspectEquipmentRemoteStatusBundle(
            character,
            RemotePlayerObjectId,
            aggregate,
            pkMode: 1);
        var inspectLength = BinaryPrimitives.ReadUInt16LittleEndian(
            bundle.AsSpan(0, sizeof(ushort)));

        Check.True(
            bundle.AsSpan(inspectLength).SequenceEqual(expectedStatus),
            "remote inspect bundle appends the exact camp/PK status vector");
    }

    private static void CheckRemoteEgressCoverage()
    {
        var gameDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Godswar.Server",
            "Game");
        var sources = Directory
            .EnumerateFiles(gameDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        var allGameSource = string.Join('\n', sources);
        var localPublishing = File.ReadAllText(Path.Combine(
            gameDirectory,
            "GameSessionRegistry.PlayerStatusPublishing.cs"));
        var localBuilder = File.ReadAllText(Path.Combine(
            gameDirectory,
            "GameClientHandler.PlayerStatus.cs"));

        Check.Equal(
            3,
            Count(allGameSource, "PacketBuilder.RemotePlayerStatusUpdate("),
            "all three direct remote 10166 egresses use the remote builder");
        Check.Equal(
            2,
            Count(
                allGameSource,
                "PacketBuilder.PlayerInspectEquipmentRemoteStatusBundle("),
            "both inspect-bundle 10166 egresses use the remote builder");
        Check.Equal(
            0,
            Count(
                allGameSource,
                "PacketBuilder.PlayerInspectEquipmentStatusBundle("),
            "no game egress uses the legacy inspect-status bundle");
        Check.Equal(
            2,
            Count(allGameSource, "PacketBuilder.PlayerStatusUpdate("),
            "the only direct legacy 10166 uses are the two local builders");
        Check.Equal(
            1,
            Count(localPublishing, "PacketBuilder.PlayerStatusUpdate("),
            "local publishing keeps the local status builder");
        Check.Equal(
            1,
            Count(localBuilder, "PacketBuilder.PlayerStatusUpdate("),
            "handler local status keeps the local builder");
        Check.Equal(
            0,
            Count(
                localPublishing + localBuilder,
                "RemotePlayerStatusUpdate("),
            "local publishing never uses the remote status builder");
    }

    private static GameCharacter CreateCharacter(byte camp) => new()
    {
        Id = 88,
        AccountId = 99,
        Name = "RemoteStatusHero",
        Profession = 1,
        Level = 200,
        Camp = camp,
        PositionX = 148f,
        PositionZ = -154f,
        Equipment = GameDefaults.DefaultEquipment(1)
    };

    private static void AssertIdentityBytes(
        byte[] packet,
        byte expectedCamp,
        string context)
    {
        Check.True(
            packet[60] == 0 &&
            packet[61] == 0 &&
            packet[CampOffset] == expectedCamp &&
            packet[63] == 0,
            $"{context} writes only validated camp inside dword 60");
    }

    private static void AssertOnlyOffsetsDiffer(
        byte[] expected,
        byte[] actual,
        params int[] offsets)
    {
        var allowed = offsets.ToHashSet();
        Check.Equal(expected.Length, actual.Length, "compared packet lengths");
        for (var index = 0; index < expected.Length; index++)
        {
            if (!allowed.Contains(index))
            {
                Check.Equal(
                    expected[index],
                    actual[index],
                    $"packet byte {index} outside the projected fields");
            }
        }
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(
            value,
            index,
            StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "GodswarServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
