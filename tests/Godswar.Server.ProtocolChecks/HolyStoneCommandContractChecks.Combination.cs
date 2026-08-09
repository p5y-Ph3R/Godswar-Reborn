using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneCommandContractChecks
{
    private static void CheckCombinationContract()
    {
        var target = CombinationStone(9030, grade: 4);
        var first = CombinationStone(9030, grade: 4, stack: 2);
        var second = CombinationStone(9030, grade: 4);
        var third = CombinationStone(9030, grade: 4, stack: 3);
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.Combine,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                16,
                target.ToCompactString(),
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                10,
                first.ToCompactString(),
                11,
                second.ToCompactString(),
                12,
                third.ToCompactString(),
                out var command),
            "Combination accepts four distinct, bounded item roles");

        var subject = new CommandSubject(7, 19);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var envelope = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
            command);
        Check.True(
            envelope.Family == CommandFamily.HolyStoneCombine &&
            HolyStoneCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "Combination uses family 43 and a valid secure envelope");

        var swapped = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            envelope.ReceivedAt,
            command with
            {
                ExpectedStoneCompactItemState =
                    second.ToCompactString(),
                ExpectedCatalystCompactItemState =
                    first.ToCompactString()
            });
        var changedThird = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            envelope.ReceivedAt,
            command with
            {
                ExpectedThirdMaterialCompactItemState =
                    (third with { Stack = 4 }).ToCompactString()
            });
        var changedThirdSlot = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            envelope.ReceivedAt,
            command with { ThirdMaterialKitBagSlot = 13 });
        Check.True(
            envelope.OperationId == swapped.OperationId &&
            envelope.OperationId == changedThird.OperationId &&
            envelope.OperationId == changedThirdSlot.OperationId &&
            envelope.RequestHash != swapped.RequestHash &&
            envelope.RequestHash != changedThird.RequestHash &&
            envelope.RequestHash != changedThirdSlot.RequestHash,
            "Combination hash binds ordered roles, all states, and slots");

        Check.True(
            !HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.Combine,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                16,
                target.ToCompactString(),
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                10,
                first.ToCompactString(),
                11,
                second.ToCompactString(),
                11,
                third.ToCompactString(),
                out _),
            "Combination rejects one slot reused for two roles");
        Check.True(
            !HolyStoneCommandEnvelope.TryCreateCommand(
                OperationId,
                HolyStoneCommandOperation.Combine,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                16,
                target.ToCompactString(),
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                10,
                first.ToCompactString(),
                11,
                second.ToCompactString(),
                12,
                "[]",
                out _),
            "Combination requires exact evidence for the fourth role");

        CheckCombinationReceiptEvidence(
            target,
            first,
            second,
            third);
    }

    private static void CheckCombinationReceiptEvidence(
        CompactItemEntry target,
        CompactItemEntry first,
        CompactItemEntry second,
        CompactItemEntry third)
    {
        Check.Equal(
            (int)HolyStoneCombinationEligibilityFailure.None,
            (int)HolyStoneCombinationPolicy.TryPrepare(
                target,
                first,
                second,
                third,
                out var plan),
            "Combination receipt fixture satisfies policy");
        var receipt = CreateCombinationReceipt(
            target,
            first,
            second,
            third,
            plan,
            new HolyStoneCombinationReceiptEvidence(
                12,
                4,
                third.ToCompactString(),
                third.ToCompactString(),
                plan.ThirdMaterialAfter.ToCompactString()));
        Check.True(
            receipt.Status == HolyStoneCommandResultStatus.Combined &&
            receipt.NativeResultSubId ==
                HolyStoneNativeResults.CombinationSucceededSubId &&
            receipt.CombinationEvidence?.ThirdMaterialItemInstanceId == 4,
            "Combination receipt carries its fourth durable identity");

        Check.Throws<ArgumentException>(
            () => CreateCombinationReceipt(
                target,
                first,
                second,
                third,
                plan,
                combinationEvidence: null),
            "committed Combination rejects missing fourth-role evidence");
        Check.Throws<ArgumentException>(
            () => CreateCombinationReceipt(
                target,
                first,
                second,
                third,
                plan,
                new HolyStoneCombinationReceiptEvidence(
                    11,
                    4,
                    third.ToCompactString(),
                    third.ToCompactString(),
                    plan.ThirdMaterialAfter.ToCompactString())),
            "Combination receipt rejects a duplicate material slot");
        Check.Throws<ArgumentException>(
            () => CreateCombinationReceipt(
                target,
                first,
                second,
                third,
                plan,
                new HolyStoneCombinationReceiptEvidence(
                    12,
                    4,
                    third.ToCompactString(),
                    third.ToCompactString(),
                    third.ToCompactString())),
            "Combination receipt rejects forged fourth-item mutation evidence");
    }

    private static HolyStoneExecutionReceipt CreateCombinationReceipt(
        CompactItemEntry target,
        CompactItemEntry first,
        CompactItemEntry second,
        CompactItemEntry third,
        HolyStoneCombinationPlan plan,
        HolyStoneCombinationReceiptEvidence? combinationEvidence) =>
        new(
            19,
            HolyStoneCommandOperation.Combine,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneCommandResultStatus.Combined,
            HolyStoneNativeResults.CombinationSucceededSubId,
            HolyStoneTargetLocation.KitBag,
            16,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            1,
            target.ToCompactString(),
            target.ToCompactString(),
            plan.TargetAfter.ToCompactString(),
            10,
            2,
            first.ToCompactString(),
            first.ToCompactString(),
            plan.FirstMaterialAfter.ToCompactString(),
            -1,
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            1,
            "42",
            Guid.Parse("913756d0-f762-44ed-b83e-0011467dbe24"),
            11,
            3,
            second.ToCompactString(),
            second.ToCompactString(),
            plan.SecondMaterialAfter.ToCompactString(),
            null,
            null,
            combinationEvidence);

    private static CompactItemEntry CombinationStone(
        uint id,
        short grade,
        short stack = 1) =>
        CompactItemEntry.Empty with
        {
            Id = id,
            Quality = 7,
            Grade = grade,
            Bound = 1,
            Stack = stack,
            Exp = 123
        };
}
