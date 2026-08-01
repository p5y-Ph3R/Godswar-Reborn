using Godswar.Server.Application.Inventory;
using Godswar.Server.Domain.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private const int BasicMaximumSockets = 2;
    private const int MaximumUsableSockets = 4;
    private const int HeatedHolyStoneItemId = 9030;

    private async Task<HolyStonePlan> CreatePlanAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            HolyStoneCommandContext context,
            LockedCharacter character,
            LockedCommandItems locked,
        CancellationToken cancellationToken)
    {
        var target = locked.Target;
        var stone = locked.Stone;
        var targetState =
            target?.Item.ToCompactString() ?? "[]";
        var stoneState =
            stone?.Item.ToCompactString() ?? "[]";
        if (target is null)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.TargetMissing,
                CompactItemEntry.Empty,
                CompactItemEntry.Empty);
        }
        if (!string.Equals(
                context.Command.ExpectedTargetCompactItemState,
                targetState,
                StringComparison.Ordinal))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StaleTarget,
                target.Item,
                stone?.Item ?? CompactItemEntry.Empty);
        }
        if (context.Command.Operation ==
                HolyStoneCommandOperation.Mount &&
            stone is null)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StoneMissing,
                target.Item,
                CompactItemEntry.Empty);
        }
        if (context.Command.Operation ==
                HolyStoneCommandOperation.Mount &&
            !string.Equals(
                context.Command.ExpectedStoneCompactItemState,
                stoneState,
                StringComparison.Ordinal))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StaleStone,
                target.Item,
                stone!.Item);
        }

        if (target.Item.Stack != 1)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.WrongSelection,
                target.Item,
                stone?.Item ?? CompactItemEntry.Empty);
        }
        if (!_itemContent.Templates.TryGet(
                target.Item.Id,
                out var targetTemplate) ||
            !string.Equals(
                targetTemplate.Kind,
                "weapon",
                StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.TargetNotEquipment,
                target.Item,
                stone?.Item ?? CompactItemEntry.Empty);
        }

        ValidateSocketState(target.Item);
        return context.Command.Operation switch
        {
            HolyStoneCommandOperation.Mount =>
                PlanMount(context, target.Item, stone!.Item),
            HolyStoneCommandOperation.Remove =>
                PlanRemove(context, target.Item, locked.KitBag),
            HolyStoneCommandOperation.Drill =>
                PlanDrill(context, target.Item, character.Gold),
            _ => throw new InvalidDataException(
                "The Holy Stone operation is unsupported.")
        };
    }

    private static HolyStonePlan PlanMount(
        HolyStoneCommandContext context,
        CompactItemEntry target,
        CompactItemEntry stone)
    {
        if (stone.Id == HeatedHolyStoneItemId)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StoneMissingSpirit,
                target,
                stone);
        }
        if (!TryResolveFireSpirit(stone.Id, out var effectId))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.StoneNotHolyStone,
                target,
                stone);
        }
        if (HasSocketEffect(target, effectId))
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.DuplicateSpirit,
                target,
                stone);
        }
        if (target.SocketCount <= 0)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.SocketNotDrilled,
                target,
                stone);
        }

        var socketIndex = FindFirstEmptyOpenedSocket(target);
        if (socketIndex < 0)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.SocketCapacityReached,
                target,
                stone);
        }

        var level = ResolveStoneLevel(stone);
        return new HolyStonePlan(
            HolyStoneCommandResultStatus.Mounted,
            socketIndex,
            SetSocket(target, socketIndex, effectId, level),
            stone.Stack == 1
                ? CompactItemEntry.Empty
                : stone with
                {
                    Stack = checked((short)(stone.Stack - 1))
                },
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            0);
    }

    private static HolyStonePlan PlanRemove(
        HolyStoneCommandContext context,
        CompactItemEntry target,
        IReadOnlyDictionary<short, LockedItem> kitBag)
    {
        var socketIndex = context.Command.SocketIndex;
        if (socketIndex < 0 ||
            socketIndex >= target.SocketCount ||
            socketIndex >= MaximumUsableSockets)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.InvalidSocket,
                target,
                CompactItemEntry.Empty);
        }

        var (effectId, level) = GetSocket(target, socketIndex);
        if (!effectId.HasValue || !level.HasValue)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.SocketEmpty,
                target,
                CompactItemEntry.Empty);
        }

        var destination = FindFirstEmptyKitBagSlot(kitBag);
        if (destination < 0)
        {
            return Rejected(
                context,
                HolyStoneCommandResultStatus.BagFull,
                target,
                CompactItemEntry.Empty);
        }

        var output = CompactItemEntry.Empty with
        {
            Id = HeatedHolyStoneItemId,
            Quality = 1,
            Grade = level.Value,
            Bound = 1,
            Stack = 1
        };
        return new HolyStonePlan(
            HolyStoneCommandResultStatus.Removed,
            socketIndex,
            SetSocket(target, socketIndex, null, null),
            CompactItemEntry.Empty,
            destination,
            output,
            effectId,
            level,
            0);
    }

    private static HolyStonePlan PlanDrill(
        HolyStoneCommandContext context,
        CompactItemEntry target,
        int gold)
    {
        if (target.SocketCount >= BasicMaximumSockets)
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
                "The basic Drill socket count is invalid.");
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

    private static HolyStonePlan Rejected(
        HolyStoneCommandContext context,
        HolyStoneCommandResultStatus status,
        CompactItemEntry target,
        CompactItemEntry stone) =>
        new(
            status,
            context.Command.SocketIndex,
            target,
            stone,
            -1,
            CompactItemEntry.Empty,
            null,
            null,
            0);

    private static int FindFirstEmptyKitBagSlot(
        IReadOnlyDictionary<short, LockedItem> kitBag)
    {
        for (short slot = 0;
             slot <= HolyStoneCommandEnvelope.MaximumKitBagSlot;
             slot++)
        {
            if (!kitBag.ContainsKey(slot))
            {
                return slot;
            }
        }
        return -1;
    }

    private static int FindFirstEmptyOpenedSocket(
        CompactItemEntry item)
    {
        for (var index = 0; index < item.SocketCount; index++)
        {
            if (!GetSocket(item, index).EffectId.HasValue)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool HasSocketEffect(
        CompactItemEntry item,
        short effectId)
    {
        for (var index = 0; index < item.SocketCount; index++)
        {
            if (GetSocket(item, index).EffectId == effectId)
            {
                return true;
            }
        }
        return false;
    }

    private static void ValidateSocketState(CompactItemEntry item)
    {
        if (item.SocketCount is < 0 or > MaximumUsableSockets)
        {
            throw new InvalidDataException(
                "The target weapon socket count is outside the " +
                "supported client contract.");
        }

        for (var index = 0; index < 6; index++)
        {
            var (effectId, level) = GetSocket(item, index);
            if (effectId.HasValue != level.HasValue ||
                effectId is <= 0 ||
                level is < 1 or > 10 ||
                index >= item.SocketCount &&
                (effectId.HasValue || level.HasValue))
            {
                throw new InvalidDataException(
                    "The target weapon has corrupt Holy Stone state.");
            }
        }
    }

    private static (short? EffectId, short? Level) GetSocket(
        CompactItemEntry item,
        int index) =>
        index switch
        {
            0 => (item.Socket1EffectId, item.Socket1Level),
            1 => (item.Socket2EffectId, item.Socket2Level),
            2 => (item.Socket3EffectId, item.Socket3Level),
            3 => (item.Socket4EffectId, item.Socket4Level),
            4 => (item.Socket5EffectId, item.Socket5Level),
            5 => (item.Socket6EffectId, item.Socket6Level),
            _ => (null, null)
        };

    private static CompactItemEntry SetSocket(
        CompactItemEntry item,
        int index,
        short? effectId,
        short? level) =>
        index switch
        {
            0 => item with
            {
                Socket1EffectId = effectId,
                Socket1Level = level
            },
            1 => item with
            {
                Socket2EffectId = effectId,
                Socket2Level = level
            },
            2 => item with
            {
                Socket3EffectId = effectId,
                Socket3Level = level
            },
            3 => item with
            {
                Socket4EffectId = effectId,
                Socket4Level = level
            },
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

    private static bool TryResolveFireSpirit(
        uint itemId,
        out short effectId)
    {
        effectId = itemId switch
        {
            9060 => 1,
            9061 => 2,
            9062 => 5,
            9063 => 6,
            9064 => 7,
            9065 => 8,
            9066 => 3,
            9067 => 4,
            9088 => 17,
            9089 => 18,
            _ => 0
        };
        return effectId > 0;
    }

    private static short ResolveStoneLevel(
        CompactItemEntry stone)
    {
        if (stone.Grade > 0)
        {
            return checked((short)Math.Clamp((int)stone.Grade, 1, 10));
        }
        if (stone.Quality > 0)
        {
            return checked((short)Math.Clamp((int)stone.Quality, 1, 10));
        }
        return 1;
    }
}
