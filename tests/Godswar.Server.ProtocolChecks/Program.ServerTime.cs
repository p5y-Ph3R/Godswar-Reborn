using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckServerTimeHandlerAsync()
    {
        var calendar = RealmCalendar.CreateForTesting(
            RealmId.Tempest,
            "Asia/Manila");
        var store = new ServerTimeGameStore();
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var handler = new GameClientHandler(
            session,
            store,
            new GameSessionRegistry(store),
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            processRealmId: RealmId.Tempest,
            realmCalendar: calendar);

        var requestBytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            requestBytes.AsSpan(0, 2),
            checked((ushort)requestBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            requestBytes.AsSpan(2, 2),
            Opcodes.ServerTimeRequest);
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await InvokeServerTimePacketAsync(
            handler,
            new GamePacket(requestBytes));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var clear = transport.WrittenBytes;
        new PacketCipher().Transform(clear);
        Check.Equal(14, clear.Length, "server-time handler emits one stock packet");
        Check.Equal(
            Opcodes.ServerTimeRequest,
            BinaryPrimitives.ReadUInt16LittleEndian(clear.AsSpan(2, 2)),
            "server-time handler preserves the stock opcode");
        Check.Equal(
            -28_800,
            BinaryPrimitives.ReadInt32LittleEndian(clear.AsSpan(4, 4)),
            "server-time handler emits its selected realm's native UTC bias");
        var emittedAt = BinaryPrimitives.ReadUInt32LittleEndian(
            clear.AsSpan(8, 4));
        Check.True(
            emittedAt >= before && emittedAt <= after,
            "server-time handler carries its current Unix timestamp");
    }

    private static async Task InvokeServerTimePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var method = typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync was not found.");
        var invocation = method.Invoke(
            handler,
            [packet, CancellationToken.None]);
        await (Task)(invocation ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync returned no task."));
    }

    private sealed class ServerTimeGameStore : GameStoreTestStub;
}
