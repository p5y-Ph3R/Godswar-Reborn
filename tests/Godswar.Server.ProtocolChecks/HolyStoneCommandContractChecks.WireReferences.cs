using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneCommandContractChecks
{
    private const string CapturedPageZeroDrillHex =
        "5C005527DB1300001E0000001E0000002D010000FFFFFFFFFFFFFFFF" +
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF10000000FFFFFFFFFFFFFFFF" +
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" +
        "FFFFFFFFFFFFFFFF";

    private static void CheckCapturedPageAwareBagReferences()
    {
        var capturedPacket = new GamePacket(
            Convert.FromHexString(CapturedPageZeroDrillHex),
            OperationId);
        Check.Equal(
            HolyStoneProtocol.PacketBytes,
            capturedPacket.Buffer.Length,
            "captured stock Drill packet length");
        Check.True(
            HolyStoneProtocol.TryReadMutation(
                capturedPacket,
                out var npcId,
                out var dialogIndex,
                out var capturedIntent),
            "captured stock Drill packet parses");
        Check.Equal(
            HolyStoneProtocol.SpartaNpcId,
            npcId,
            "captured stock Drill NPC");
        Check.Equal(
            HolyStoneProtocol.DialogIndex,
            dialogIndex,
            "captured stock Drill dialogue");
        Check.Equal(
            (int)HolyStoneCommandOperation.Drill,
            (int)capturedIntent.Operation,
            "captured stock Drill operation");
        Check.Equal(
            (int)HolyStoneTargetLocation.KitBag,
            (int)capturedIntent.TargetLocation,
            "captured stock Drill uses a kitbag target");
        Check.Equal(
            16,
            capturedIntent.TargetSlot,
            "captured stock Drill target slot");
        Check.Equal(
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            capturedIntent.SocketIndex,
            "captured stock Drill delegates socket choice");

        foreach (var mapping in new[]
                 {
                     (Reference: 16, Slot: 16),
                     (Reference: 100, Slot: 24),
                     (Reference: 107, Slot: 31),
                     (Reference: 116, Slot: 40),
                     (Reference: 205, Slot: 53)
                 })
        {
            var mappedDrill = CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.DrillSubId,
                args => args[HolyStoneProtocol.TargetArgumentIndex] =
                    mapping.Reference);
            Check.True(
                HolyStoneProtocol.TryReadMutation(
                    mappedDrill,
                    out _,
                    out _,
                    out var mappedIntent),
                $"page-aware reference {mapping.Reference} parses");
            Check.Equal(
                mapping.Slot,
                mappedIntent.TargetSlot,
                $"page-aware reference {mapping.Reference} slot");
            Check.Equal(
                mapping.Reference,
                HolyStoneProtocol.EncodeKitBagReference(mapping.Slot),
                $"slot {mapping.Slot} encodes canonically");
        }
    }
}
