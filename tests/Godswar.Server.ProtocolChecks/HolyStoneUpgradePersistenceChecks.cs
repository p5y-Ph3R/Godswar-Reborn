using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class HolyStoneUpgradePersistenceChecks
{
    public const string CheckName =
        "Holy Stone Upgrade persisted outcome evidence";

    public static Task RunAsync()
    {
        var target = Item(9031, 8);
        var eclipse = Item(9042, stack: 2);
        var catalyst = Item(9050, stack: 2);
        HolyStoneUpgradePolicy.TryPrepare(
            target,
            eclipse,
            catalyst,
            out var attempt);
        var resolution = attempt.Resolve(
            target,
            eclipse,
            catalyst,
            roll: 20);
        var eventId = Guid.NewGuid();
        var receipt = new HolyStoneExecutionReceipt(
            characterId: 7,
            HolyStoneCommandOperation.Upgrade,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneCommandResultStatus.UpgradeFailedDowngraded,
            HolyStoneNativeResults.UpgradeFailedDowngradedSubId,
            HolyStoneTargetLocation.KitBag,
            targetSlot: 16,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            targetItemInstanceId: 101,
            target.ToCompactString(),
            target.ToCompactString(),
            resolution.TargetAfter.ToCompactString(),
            stoneKitBagSlot: 10,
            stoneItemInstanceId: 102,
            eclipse.ToCompactString(),
            eclipse.ToCompactString(),
            resolution.EclipseStoneAfter.ToCompactString(),
            outputKitBagSlot: -1,
            outputItemInstanceId: null,
            outputBeforeCompactItemState: null,
            outputAfterCompactItemState: null,
            goldSpent: 0,
            goldBefore: 500,
            goldAfter: 500,
            walletRevision: 0,
            inventoryRevision: 1,
            auditReference: "123",
            outboxEventId: eventId,
            catalystKitBagSlot: 11,
            catalystItemInstanceId: 103,
            catalyst.ToCompactString(),
            catalyst.ToCompactString(),
            resolution.CatalystAfter.ToCompactString(),
            upgradeRoll: 20,
            upgradeSuccessRate: 20);

        var encoded = HolyStonePersistenceCodec.Encode(receipt);
        var decoded = HolyStonePersistenceCodec.Decode(encoded);
        Check.Equal(receipt, decoded, "Upgrade receipt round trip");
        Check.True(
            decoded.CatalystItemInstanceId == 103 &&
            decoded.UpgradeRoll == 20 &&
            decoded.UpgradeSuccessRate == 20,
            "stored receipt retains catalyst and random outcome evidence");

        var tampered = JsonNode.Parse(encoded)!.AsObject();
        tampered["upgradeRoll"] = 0;
        Check.Throws<ArgumentException>(
            () => HolyStonePersistenceCodec.Decode(
                Encoding.UTF8.GetBytes(tampered.ToJsonString())),
            "tampered Upgrade roll cannot validate against persisted state");

        AssertEnvelopeBindsCatalyst(target, eclipse, catalyst);
        return Task.CompletedTask;
    }

    private static void AssertEnvelopeBindsCatalyst(
        CompactItemEntry target,
        CompactItemEntry eclipse,
        CompactItemEntry catalyst)
    {
        var operationId = Guid.NewGuid();
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                operationId,
                HolyStoneCommandOperation.Upgrade,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                16,
                target.ToCompactString(),
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                10,
                eclipse.ToCompactString(),
                11,
                catalyst.ToCompactString(),
                out var withCatalyst),
            "Upgrade command accepts three distinct authoritative slots");
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                operationId,
                HolyStoneCommandOperation.Upgrade,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                16,
                target.ToCompactString(),
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                10,
                eclipse.ToCompactString(),
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                "[]",
                out var withoutCatalyst),
            "Upgrade command permits no catalyst");
        var subject = new CommandSubject(3, 7);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var first = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            withCatalyst);
        var second = HolyStoneCommandEnvelope.Create(
            subject,
            connection,
            DateTimeOffset.UtcNow,
            withoutCatalyst);
        Check.True(
            first.Family == CommandFamily.HolyStoneUpgrade &&
            first.RequestHash != second.RequestHash,
            "Upgrade request identity binds catalyst slot and state");
        Check.True(
            !HolyStoneCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                HolyStoneCommandOperation.Upgrade,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                16,
                target.ToCompactString(),
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                10,
                eclipse.ToCompactString(),
                10,
                catalyst.ToCompactString(),
                out _),
            "Upgrade rejects aliased Eclipse and catalyst slots");
    }

    private static CompactItemEntry Item(
        uint id,
        short grade = 1,
        short stack = 1) =>
        CompactItemEntry.Empty with
        {
            Id = id,
            Quality = 1,
            Grade = grade,
            Stack = stack
        };
}
