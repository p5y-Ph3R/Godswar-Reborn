using System.Buffers.Binary;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static readonly int[] HolyStoneInitialMenuSubIds =
        [101, 201, 301, 401, 501, 601, 701, 801];

    private static async Task CheckRawInitialMenuNavigationAsync()
    {
        foreach (var npcId in new[]
                 {
                     HolyStoneProtocol.SpartaNpcId,
                     HolyStoneProtocol.AthensNpcId
                 })
        {
            await using var fixture = await CreateRawFixtureAsync(
                requestNpcId: npcId);
            var packet = CreateCapturedInitialMenuPacket(npcId);

            Check.True(
                HolyStoneProtocol.IsExactPageNavigation(packet),
                $"Holy Stone initial menu accepts captured NPC {npcId} shape");
            await InvokeAsync(fixture.Handler, packet);

            Check.Equal(
                0,
                fixture.Store.HolyStoneCount,
                $"Holy Stone initial menu for NPC {npcId} cannot mutate");
            var response = fixture.Transport.ReadLegacyPackets().Single();
            Check.Equal(
                12 +
                    (HolyStoneInitialMenuSubIds.Length * sizeof(int)),
                response.Length,
                $"Holy Stone initial menu response length for NPC {npcId}");
            for (var index = 0;
                 index < HolyStoneInitialMenuSubIds.Length;
                 index++)
            {
                Check.Equal(
                    HolyStoneInitialMenuSubIds[index],
                    BinaryPrimitives.ReadInt32LittleEndian(
                        response.AsSpan(
                            12 + (index * sizeof(int)),
                            sizeof(int))),
                    $"Holy Stone initial menu entry {index} for NPC {npcId}");
            }
        }

        var captured = CreateCapturedInitialMenuPacket(
            HolyStoneProtocol.SpartaNpcId);
        await AssertRawRejectedAsync(
            ResizePacket(captured, -sizeof(int)),
            "short Holy Stone initial menu");
        await AssertRawRejectedAsync(
            ResizePacket(captured, sizeof(int)),
            "oversized Holy Stone initial menu");

        var mismatchedDialog = captured.Buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            mismatchedDialog.AsSpan(12, sizeof(int)),
            HolyStoneProtocol.DialogIndex + 1);
        await AssertRawRejectedAsync(
            new Godswar.Server.Protocol.GamePacket(mismatchedDialog),
            "Holy Stone initial menu duplicate-dialog mismatch");
    }

    private static Godswar.Server.Protocol.GamePacket
        CreateCapturedInitialMenuPacket(uint npcId) =>
        HolyStoneCommandContractChecks.CreatePacket(
            npcId,
            HolyStoneProtocol.InitialMenuRequestSubId,
            static args =>
            {
                // The stock client leaves runtime scratch values in these
                // unused fields when opening "Holy Stone Craft". This is the
                // exact argument area captured from the working server.
                args[6] = 80_407_956;
                args[7] = 659_788_784;
                args[8] = 80_407_956;
                args[9] = 659_788_796;
                args[10] = 0;
                args[11] = 598_370_584;
                args[12] = 0;
                args[13] = -866_889_794;
                args[14] = 640_494_704;
                args[15] = 1_767_948;
                args[16] = 8_458_971;
            });
}
