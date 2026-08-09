using Godswar.Server.Application.Inventory;
using Godswar.Server.Domain.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private const short MaximumMountGearSockets = 2;

    private static HolyStonePlan PlanMountGearDrill(
        HolyStoneCommandContext context,
        CompactItemEntry target,
        int gold)
    {
        if (target.SocketCount >= MaximumMountGearSockets)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.MaximumSockets,
                target,
                CompactItemEntry.Empty);
        }

        if (!HolyStoneDrillCostPolicy.TryGetGoldCost(
                target.SocketCount,
                out var goldCost))
        {
            throw new InvalidDataException(
                "The mount-gear socket count has no drilling cost.");
        }
        if (gold < goldCost)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.InsufficientFunds,
                target,
                CompactItemEntry.Empty);
        }

        return new HolyStonePlan(
            HolyStoneCommandResultStatus.Drilled,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            target with
            {
                SocketCount = checked((short)(target.SocketCount + 1))
            },
            CompactItemEntry.Empty,
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            goldCost);
    }

    private static void ValidateSocketState(
        CompactItemEntry item,
        bool isMountGear)
    {
        var maximumSockets = isMountGear
            ? MaximumMountGearSockets
            : MaximumUsableSockets;
        if (item.SocketCount < 0 || item.SocketCount > maximumSockets)
        {
            throw new InvalidDataException(
                "The target equipment socket count is outside its " +
                "supported client contract.");
        }

        for (var index = 0; index < 6; index++)
        {
            var (effectId, level, value) = GetSocket(item, index);
            var isZephyrEffect = effectId is
                >= ZephyrSpiritEffects.DaedalusAttunement and
                <= ZephyrSpiritEffects.ThemisContinuity;
            if (effectId.HasValue != level.HasValue ||
                value.HasValue && !effectId.HasValue ||
                value is <= 0 ||
                effectId is <= 0 ||
                level is < 1 or > 10 ||
                index >= item.SocketCount &&
                (effectId.HasValue || level.HasValue || value.HasValue) ||
                effectId.HasValue && isMountGear != isZephyrEffect)
            {
                throw new InvalidDataException(
                    "The target equipment has corrupt Holy Stone state.");
            }
        }
    }
}
