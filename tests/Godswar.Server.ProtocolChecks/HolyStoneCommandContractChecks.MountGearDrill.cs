using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneCommandContractChecks
{
    private static void CheckMountGearDrillContract()
    {
        Check.Equal(
            45,
            (int)HolyStoneCommandEnvelope.Family(
                HolyStoneCommandOperation.MountGearDrill),
            "Mount Gear Drill has a stable command family");
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.MountGearDrill),
            "Mount Gear Drill requires a client operation UUID");
        Check.Equal(
            "mount_gear_drill",
            CommandMetrics.FamilyCode(CommandFamily.MountGearDrill),
            "Mount Gear Drill has a bounded metric family");
        Check.Equal(
            HolyStoneNativeResults.TargetNotMountGearSubId,
            HolyStoneNativeResults.GetResultSubId(
                HolyStoneCommandOperation.MountGearDrill,
                HolyStoneCommandResultStatus.TargetNotEquipment),
            "Mount Gear Drill reports that character gear is not mount gear");

        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.MountGearDrill,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                targetSlot: 53,
                expectedTargetCompactItemState: "[]",
                socketIndex:
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                stoneKitBagSlot:
                    HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                expectedStoneCompactItemState: "[]",
                out var command),
            "Mount Gear Drill accepts one bounded bag target");
        Check.True(
            !HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.MountGearDrill,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.Equipment,
                HolyStoneCommandEnvelope.WeaponEquipmentSlot,
                expectedTargetCompactItemState: "[]",
                socketIndex:
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                stoneKitBagSlot:
                    HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                expectedStoneCompactItemState: "[]",
                out _),
            "Mount Gear Drill cannot target equipped rows");

        var packet = CreatePacket(
            HolyStoneProtocol.AthensNpcId,
            HolyStoneProtocol.MountGearDrillSubId,
            args => args[HolyStoneProtocol.TargetArgumentIndex] =
                HolyStoneProtocol.EncodeKitBagReference(53));
        Check.True(
            HolyStoneProtocol.TryReadMutation(
                packet,
                out var npcId,
                out var dialogIndex,
                out var intent) &&
            npcId == HolyStoneProtocol.AthensNpcId &&
            dialogIndex == HolyStoneProtocol.DialogIndex &&
            intent.Operation ==
                HolyStoneCommandOperation.MountGearDrill &&
            intent.TargetLocation == HolyStoneTargetLocation.KitBag &&
            intent.TargetSlot == 53 &&
            intent.StoneKitBagSlot ==
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
            "exact action 801 parses as Mount Gear Drill");

        var strayArgument = CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.MountGearDrillSubId,
            args =>
            {
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(53);
                args[HolyStoneProtocol.StoneArgumentIndex] = 0;
            });
        Check.True(
            !HolyStoneProtocol.TryReadMutation(
                strayArgument,
                out _,
                out _,
                out _),
            "action 801 rejects unowned argument roles");

        var subject = new CommandSubject(7, 19);
        Check.True(
            HolyStoneCommandEnvelope.CreateOperationId(
                subject,
                HolyStoneCommandOperation.MountGearDrill,
                OperationId) !=
            HolyStoneCommandEnvelope.CreateOperationId(
                subject,
                HolyStoneCommandOperation.Drill,
                OperationId),
            "one UUID cannot alias character and mount-gear Drill");

        var envelope = HolyStoneCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)HolyStoneCommandEnvelope.Validate(envelope),
            "Mount Gear Drill secure envelope validates");
    }
}
