using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneCommandContractChecks
{
    private static void CheckAdvancedDrillContract()
    {
        Check.Equal(
            41,
            (int)HolyStoneCommandEnvelope.Family(
                HolyStoneCommandOperation.AdvancedDrill),
            "Advanced Drill has a distinct command family");
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.HolyStoneAdvancedDrill),
            "Advanced Drill requires a client operation UUID");

        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.AdvancedDrill,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot: 53,
                expectedTargetCompactItemState: "[]",
                socketIndex:
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                stoneKitBagSlot: 95,
                expectedStoneCompactItemState: "[]",
                out _),
            "bounded Advanced Drill command is accepted");
        Check.True(
            !HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.AdvancedDrill,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot: 53,
                expectedTargetCompactItemState: "[]",
                socketIndex:
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                stoneKitBagSlot: 53,
                expectedStoneCompactItemState: "[]",
                out _),
            "Advanced Drill cannot consume its target as a Socket Spell");

        var advanced = CreatePacket(
            HolyStoneProtocol.AthensNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            args =>
            {
                args[HolyStoneProtocol.AdvancedDrillScratchArgumentIndex] =
                    0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(53);
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(95);
            });
        Check.True(
            HolyStoneProtocol.TryReadMutation(
                advanced,
                out _,
                out _,
                out var intent) &&
            intent.Operation ==
                HolyStoneCommandOperation.AdvancedDrill &&
            intent.TargetLocation == HolyStoneTargetLocation.KitBag &&
            intent.TargetSlot == 53 &&
            intent.StoneKitBagSlot == 95 &&
            intent.SocketIndex ==
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            "Action 701 requires scratch arg 0 and decodes gear arg 6 " +
            "and Socket Spell arg 7");

        var missingScratch = CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            args =>
            {
                args[HolyStoneProtocol.TargetArgumentIndex] = 0;
                args[HolyStoneProtocol.StoneArgumentIndex] = 1;
            });
        Check.True(
            !HolyStoneProtocol.TryReadMutation(
                missingScratch,
                out _,
                out _,
                out _),
            "Action 701 rejects a mutation without scratch arg 0");

        var navigation = CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            _ => { });
        Check.True(
            HolyStoneProtocol.IsExactAdvancedDrillNavigation(navigation) &&
            !HolyStoneProtocol.TryReadMutation(
                navigation,
                out _,
                out _,
                out _),
            "empty Action 701 remains a page request");

        var stray = CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            args =>
            {
                args[HolyStoneProtocol.TargetArgumentIndex] = 0;
                args[HolyStoneProtocol.StoneArgumentIndex] = 1;
                args[HolyStoneProtocol.AdvancedDrillScratchArgumentIndex] =
                    0;
                args[3] = 0;
            });
        Check.True(
            !HolyStoneProtocol.TryReadMutation(
                stray,
                out _,
                out _,
                out _),
            "Action 701 rejects every argument outside 0, 6, and 7");
    }
}
