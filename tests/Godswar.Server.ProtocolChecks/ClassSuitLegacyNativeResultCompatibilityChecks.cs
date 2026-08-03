using System.Text;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static class ClassSuitLegacyNativeResultCompatibilityChecks
{
    public static void Run()
    {
        var cases = new[]
        {
            (Operation: ClassSuitCommandOperation.AddAttribute,
                Status: ClassSuitCommandResultStatus.Succeeded,
                CurrentResult: 121,
                LegacyResult: 119,
                AuditId: 43L),
            (Operation: ClassSuitCommandOperation.DeleteAttribute,
                Status: ClassSuitCommandResultStatus.Succeeded,
                CurrentResult: 122,
                LegacyResult: 121,
                AuditId: 44L),
            (Operation: ClassSuitCommandOperation.DeleteAttribute,
                Status: ClassSuitCommandResultStatus.AttributeMissing,
                CurrentResult: 140,
                LegacyResult: 122,
                AuditId: 45L),
            (Operation: ClassSuitCommandOperation.AddAttribute,
                Status: ClassSuitCommandResultStatus.InvalidMaterial,
                CurrentResult: 144,
                LegacyResult: 116,
                AuditId: 46L)
        };
        foreach (var value in cases)
        {
            CheckLegacyReceipt(value);
        }
    }

    private static void CheckLegacyReceipt(
        (ClassSuitCommandOperation Operation,
            ClassSuitCommandResultStatus Status,
            int CurrentResult,
            int LegacyResult,
            long AuditId) value)
    {
        var family = ClassSuitCommandEnvelope.Family(value.Operation);
        var succeeded =
            value.Status == ClassSuitCommandResultStatus.Succeeded;
        ClassSuitReceiptMutation[] mutations = succeeded
            ?
            [
                new ClassSuitReceiptMutation(
                    KitBagSlot: 0,
                    BeforeItemId: 1035,
                    AfterItemId: 1035,
                    BeforeCompactItemState: "gear-before",
                    AfterCompactItemState: "gear-after")
            ]
            : [];
        var receipt = new ClassSuitExecutionReceipt(
            family,
            CharacterId: 13,
            value.Operation,
            ClassSuitCommandEnvelope.SpartaNpcId,
            ClassSuitCommandEnvelope.DialogIndex,
            CreateReplayIntent(value.Operation),
            value.Status,
            value.CurrentResult,
            Mutations: mutations,
            InventoryRevision: 43,
            AuditReference: value.AuditId.ToString(),
            OutboxEventId: succeeded ? Guid.NewGuid() : null);
        var storedJson = Encoding.UTF8.GetString(
            ClassSuitPersistenceCodec.Encode(receipt));
        Check.True(
            storedJson.Contains(
                "\"contractVersion\":2",
                StringComparison.Ordinal) &&
            storedJson.Contains(
                $"\"nativeResultSubId\":{value.LegacyResult}",
                StringComparison.Ordinal),
            $"{value.Operation}/{value.Status} remains rollback-safe v2 evidence");

        var normalized = ClassSuitPersistenceCodec.DecodeAndVerify(
            storedJson,
            ClassSuitPersistenceCodec.Hash(
                Encoding.UTF8.GetBytes(storedJson)),
            expectedResultCode: succeeded
                ? "committed"
                : "terminal_rejected",
            expectedAuditId: value.AuditId,
            expectedFamily: family);
        Check.Equal(
            value.CurrentResult,
            normalized.NativeResultSubId,
            $"{value.Operation} legacy receipt replays with corrected client text");
        Check.Throws<InvalidDataException>(
            () => ClassSuitPersistenceCodec.Encode(
                receipt with
                {
                    NativeResultSubId = value.LegacyResult
                }),
            $"{value.Operation} new receipt cannot persist a legacy result ID");

        if (value.Operation == ClassSuitCommandOperation.AddAttribute &&
            value.Status == ClassSuitCommandResultStatus.Succeeded)
        {
            var crossOperationJson = storedJson.Replace(
                $"\"nativeResultSubId\":{value.LegacyResult}",
                "\"nativeResultSubId\":122",
                StringComparison.Ordinal);
            Check.Throws<InvalidDataException>(
                () => ClassSuitPersistenceCodec.Decode(
                    Encoding.UTF8.GetBytes(crossOperationJson)),
                "v2 compatibility rejects a result ID from another operation");
        }
    }

    private static ClassSuitReplayIntent CreateReplayIntent(
        ClassSuitCommandOperation operation)
    {
        Check.True(
            ClassSuitReplayIntent.TryCreate(
                operation,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                gearKitBagSlot: 0,
                primaryMaterialKitBagSlot: 1,
                secondaryMaterialKitBagSlot: 2,
                out var intent),
            "legacy Class Suit replay fixture intent is valid");
        return intent;
    }
}
