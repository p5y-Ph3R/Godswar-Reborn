using System.Globalization;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private async Task<HolyStoneExecutionReceipt>
        PersistTerminalResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            HolyStoneCommandContext context,
            LockedCharacter character,
            LockedCommandItems locked,
            HolyStonePlan plan,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            character,
            locked,
            plan,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        await ReachAsync(
            PostgresHolyStoneCommandStage.AuditInserted,
            -1,
            cancellationToken);
        var receipt = CreateReceipt(
            context,
            locked,
            plan,
            character.Gold,
            character.WalletRevision,
            character.InventoryRevision,
            auditId,
            eventId: null,
            outputItemInstanceId: null);
        var payload = HolyStonePersistenceCodec.Encode(receipt);
        await InsertInboxAsync(
            connection,
            transaction,
            context.Command.Operation,
            plan.Status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresHolyStoneCommandStage.InboxInserted,
            -1,
            cancellationToken);
        return receipt;
    }

    private static HolyStoneExecutionReceipt CreateReceipt(
        HolyStoneCommandContext context,
        LockedCommandItems locked,
        HolyStonePlan plan,
        int goldBefore,
        long walletRevision,
        long inventoryRevision,
        long auditId,
        Guid? eventId,
        long? outputItemInstanceId)
    {
        var targetBefore =
            locked.Target?.Item.ToCompactString() ?? "[]";
        var stoneBefore =
            locked.Stone?.Item.ToCompactString() ?? "[]";
        var catalystBefore =
            locked.Catalyst?.Item.ToCompactString() ?? "[]";
        var thirdMaterialBefore =
            locked.ThirdMaterial?.Item.ToCompactString() ?? "[]";
        var success = plan.IsSuccess;
        var targetAfter = success
            ? plan.TargetAfter.ToCompactString()
            : targetBefore;
        var stoneAfter =
            context.Command.Operation is
                HolyStoneCommandOperation.Mount or
                HolyStoneCommandOperation.AdvancedDrill or
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit
                ? success
                    ? plan.StoneAfter.ToCompactString()
                    : stoneBefore
                : "[]";
        var hasOutput =
            plan.Status == HolyStoneCommandResultStatus.Removed;
        var catalystAfter =
            context.Command.Operation is
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit
                ? success
                    ? plan.CatalystAfter.ToCompactString()
                    : catalystBefore
                : "[]";
        var combinationEvidence =
            context.Command.Operation == HolyStoneCommandOperation.Combine
                ? new HolyStoneCombinationReceiptEvidence(
                    context.Command.ThirdMaterialKitBagSlot,
                    locked.ThirdMaterial?.ItemInstanceId,
                    context.Command.ExpectedThirdMaterialCompactItemState,
                    thirdMaterialBefore,
                    success
                        ? plan.ThirdMaterialAfter.ToCompactString()
                        : thirdMaterialBefore)
                : null;
        return new HolyStoneExecutionReceipt(
            context.Subject.CharacterId,
            context.Command.Operation,
            context.Command.NpcId,
            context.Command.DialogIndex,
            plan.Status,
            HolySpiritNativeResult.GetResultSubId(
                context.Command.Operation,
                plan.Status,
                targetBefore,
                targetAfter,
                stoneBefore),
            context.Command.TargetLocation,
            context.Command.TargetSlot,
            plan.SocketIndex,
            locked.Target?.ItemInstanceId,
            context.Command.ExpectedTargetCompactItemState,
            targetBefore,
            targetAfter,
            context.Command.StoneKitBagSlot,
            locked.Stone?.ItemInstanceId,
            context.Command.ExpectedStoneCompactItemState,
            stoneBefore,
            stoneAfter,
            hasOutput ? plan.OutputKitBagSlot : -1,
            hasOutput ? outputItemInstanceId : null,
            outputBeforeCompactItemState: null,
            hasOutput
                ? plan.OutputItem.ToCompactString()
                : null,
            plan.GoldSpent,
            goldBefore,
            checked(goldBefore - plan.GoldSpent),
            walletRevision,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId,
            context.Command.CatalystKitBagSlot,
            locked.Catalyst?.ItemInstanceId,
            context.Command.ExpectedCatalystCompactItemState,
            catalystBefore,
            catalystAfter,
            plan.UpgradeRoll,
            plan.UpgradeSuccessRate,
            combinationEvidence);
    }
}
