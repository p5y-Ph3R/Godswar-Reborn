using System.Text;
using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.Application.Inventory;

internal sealed partial record HolyStoneExecutionReceipt
{
    private static void ValidateOutcomeEvidence(
        HolyStoneCommandResultStatus status,
        long? targetItemInstanceId,
        long inventoryRevision,
        Guid? outboxEventId)
    {
        var success = HolyStoneNativeResults.IsSuccess(status);
        if (success != outboxEventId.HasValue ||
            (success &&
             (outboxEventId == Guid.Empty ||
              !targetItemInstanceId.HasValue ||
              inventoryRevision <= 0)))
        {
            throw new ArgumentException(
                "Only a successful Holy Stone operation may publish an " +
                "inventory event.");
        }
    }

    private static void ValidateWalletEvidence(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
        string targetBefore,
        int goldSpent,
        long walletRevision)
    {
        if (status != HolyStoneCommandResultStatus.Drilled)
        {
            if (goldSpent != 0)
            {
                throw new ArgumentException(
                    "Only a successful Drill may spend Gold.");
            }
            return;
        }

        if (operation == HolyStoneCommandOperation.AdvancedDrill)
        {
            if (goldSpent != 0)
            {
                throw new ArgumentException(
                    "Advanced Drill must not spend Gold.");
            }
            return;
        }

        var hasGoldCost =
            HolyStoneDrillCostPolicy
                .TryGetGoldCostFromCompactTargetState(
            targetBefore,
            out var expectedCost);
        if (operation is not (
                HolyStoneCommandOperation.Drill or
                HolyStoneCommandOperation.MountGearDrill) ||
            !hasGoldCost ||
            goldSpent != expectedCost ||
            walletRevision <= 0)
        {
            throw new ArgumentException(
                "The Drill Gold evidence is inconsistent.");
        }
    }

    private static bool IsValidTargetSlot(
        HolyStoneTargetLocation location,
        int slot) =>
        location switch
        {
            HolyStoneTargetLocation.Equipment =>
                slot == HolyStoneCommandEnvelope.WeaponEquipmentSlot,
            HolyStoneTargetLocation.KitBag =>
                slot is
                    >= HolyStoneCommandEnvelope.MinimumKitBagSlot and
                    <= HolyStoneCommandEnvelope.MaximumKitBagSlot,
            _ => false
        };

    private static bool IsValidInstanceId(long? value) =>
        !value.HasValue || value.Value > 0;

    private static bool IsOptionalCompactState(string? value) =>
        value is null || IsBoundedCompactState(value, allowEmpty: true);

    private static bool IsBoundedCompactState(
        string? value,
        bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']' ||
            (!allowEmpty && value == "[]"))
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(value) <=
            HolyStoneCommandEnvelope.MaximumCompactItemStateUtf8Bytes;
    }

    private static void ValidateAuditReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) >
                MaximumAuditReferenceUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
